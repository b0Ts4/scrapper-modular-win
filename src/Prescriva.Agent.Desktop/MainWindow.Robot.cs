using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Desktop.Playbooks;
using Prescriva.Agent.Desktop.Shell;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Jobs;
using Prescriva.Agent.Infrastructure.Playbooks;
using Prescriva.Agent.Infrastructure.Security;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Desktop;

/// <summary>
/// Step 6, the robot (milestone 2): its settings, its job queue and local API, the CRM link, the
/// banner shown while it works, and each pharmacy's choices for the program's notices. Runs in
/// the background too (started with Windows): when switched on, a round every few seconds fetches
/// the CRM's jobs, runs the queue and sends the results back.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan RobotInterval = TimeSpan.FromSeconds(5);

    /// <summary>Playbooks shipped with the Agent: read-only JSON next to the executable.</summary>
    internal static string ShippedPlaybooksDirectory => Path.Combine(AppContext.BaseDirectory, "Playbooks");

    /// <summary>The pharmacy's notice choices and the notices the robot met, per playbook.</summary>
    internal static JsonDialogPolicyStore DialogPolicies { get; } = new(Path.Combine(DataDirectory, "playbooks"));

    private readonly HttpClient _crmHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private NoticeSettingsViewModel? _notices;
    private bool _robotLoaded;
    private JsonRobotConfigurationStore? _robotSettingsStore;
    private RobotConfiguration? _robotConfiguration;
    private SqliteJobStore? _jobStore;
    private LocalJobApi? _localApi;
    private DispatcherTimer? _robotTimer;
    private CancellationTokenSource? _robotStop;
    private RobotBanner? _robotBanner;
    private bool _robotRoundRunning;

    /// <summary>Loads the robot's settings and starts it when it was left on.</summary>
    private async Task InitializeRobotAsync()
    {
        try
        {
            var protector = new DpapiPayloadProtector();
            _robotSettingsStore = new JsonRobotConfigurationStore(Path.Combine(DataDirectory, "robot.json"), protector);
            _jobStore = new SqliteJobStore(Path.Combine(DataDirectory, "robot.db"), protector);
            _robotConfiguration = await _robotSettingsStore.LoadAsync(CancellationToken.None);
            ShowRobotSettings(_robotConfiguration);
            ApplyRobot();
            await RefreshRobotJobsAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            RobotStatusText.Text = "Não foi possível carregar as configurações do robô: " + exception.GetType().Name;
        }
    }

    private void ShowRobotSettings(RobotConfiguration configuration)
    {
        RobotEnabledCheckBox.IsChecked = configuration.Enabled;
        RobotLevelCombo.SelectedIndex = configuration.Level == RobotLevel.Saves ? 1 : 0;
        RobotEmployeeBox.Text = configuration.EmployeeCode;
        CrmUrlBox.Text = configuration.CrmUrl ?? string.Empty;
        CrmTokenBox.Password = configuration.CrmToken ?? string.Empty;
        LocalApiText.Text = $"API local para o CRM na rede da farmácia: http://127.0.0.1:{configuration.LocalApiPort}/v1/jobs (com a chave da API local).";
    }

    private async void SaveRobotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_robotSettingsStore is null || _robotConfiguration is null)
        {
            return;
        }

        var crmUrl = CrmUrlBox.Text.Trim();
        if (crmUrl.Length > 0 && (!Uri.TryCreate(crmUrl, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http")))
        {
            RobotStatusText.Text = "O endereço do CRM precisa começar com https://.";
            return;
        }

        var enabled = RobotEnabledCheckBox.IsChecked == true;
        if (enabled && string.IsNullOrWhiteSpace(RobotEmployeeBox.Text))
        {
            RobotStatusText.Text = "Informe o código do funcionário que o robô usa.";
            return;
        }

        _robotConfiguration = _robotConfiguration with
        {
            Enabled = enabled,
            Level = RobotLevelCombo.SelectedIndex == 1 ? RobotLevel.Saves : RobotLevel.Assisted,
            EmployeeCode = RobotEmployeeBox.Text.Trim(),
            CrmUrl = crmUrl.Length == 0 ? null : (crmUrl.EndsWith('/') ? crmUrl : crmUrl + "/"),
            CrmToken = CrmTokenBox.Password.Length == 0 ? null : CrmTokenBox.Password,
        };
        await _robotSettingsStore.SaveAsync(_robotConfiguration, CancellationToken.None);
        ApplyRobot();
    }

    private void CopyLocalTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_robotConfiguration is not null)
        {
            Clipboard.SetText(_robotConfiguration.LocalApiToken);
            RobotStatusText.Text = "Chave da API local copiada.";
        }
    }

    private void StopRobotJobButton_Click(object sender, RoutedEventArgs e) => StopRobotJob();

    private void StopRobotJob()
    {
        _robotStop?.Cancel();
        RobotStatusText.Text = "Parando: o robô deixa a tela como está.";
    }

    /// <summary>Starts or stops the robot's local API and its rounds to match the settings.</summary>
    private void ApplyRobot()
    {
        _localApi?.Dispose();
        _localApi = null;
        _robotTimer?.Stop();
        if (_robotConfiguration is not { Enabled: true } configuration || _jobStore is null)
        {
            RobotStatusText.Text = "Robô desligado.";
            return;
        }

        try
        {
            _localApi = new LocalJobApi(configuration.LocalApiPort, configuration.LocalApiToken, _jobStore, () => Dispatcher.BeginInvoke(RunRobotRound));
            _localApi.Start();
        }
        catch (System.Net.HttpListenerException)
        {
            _localApi = null;
            RobotStatusText.Text = $"A porta {configuration.LocalApiPort} está em uso: a API local não foi aberta.";
        }

        _robotTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = RobotInterval };
        _robotTimer.Tick -= OnRobotTick;
        _robotTimer.Tick += OnRobotTick;
        _robotTimer.Start();
        RobotStatusText.Text = configuration.CrmUrl is null
            ? "Robô ligado: aguardando trabalhos pela API local."
            : "Robô ligado: buscando trabalhos no CRM e pela API local.";
    }

    private void OnRobotTick(object? sender, EventArgs e) => RunRobotRound();

    /// <summary>A pharmacy's own playbook (in the Agent's data) or one shipped with the Agent.</summary>
    private static Task<Playbook> LoadPlaybookAsync(string id)
    {
        var own = Path.Combine(DataDirectory, "playbooks", id + ".json");
        return JsonPlaybookStore.LoadAsync(File.Exists(own) ? own : Path.Combine(ShippedPlaybooksDirectory, id + ".json"), CancellationToken.None);
    }

    private async void RunRobotRound()
    {
        if (_robotRoundRunning || _robotConfiguration is not { Enabled: true } configuration || _jobStore is null)
        {
            return;
        }

        _robotRoundRunning = true;
        _robotStop = new CancellationTokenSource();
        try
        {
            var processor = new JobProcessor(
                _jobStore,
                LoadPlaybookAsync,
                DialogPolicies,
                configuration.ToSettings,
                (playbook, data, token) => new PlaybookRunner(new Win32PlaybookDriver()).RunAsync(playbook, data, token));
            IJobConnection? connection = configuration.CrmUrl is { } url && configuration.CrmToken is { } token
                ? new CrmJobConnector(_crmHttp, new Uri(url), token, _jobStore)
                : null;
            var loop = new RobotLoop(processor, connection);
            loop.JobStarted += job => Dispatcher.Invoke(() =>
            {
                _robotBanner ??= new RobotBanner(() => Dispatcher.Invoke(StopRobotJob));
                _robotBanner.ShowWorking($"Robô trabalhando: {job.Summary} (trabalho {job.Id}). Não mexa no programa da farmácia.");
                StopRobotJobButton.IsEnabled = true;
                RobotStatusText.Text = $"Trabalhando no trabalho {job.Id}.";
            });
            loop.JobFinished += job => Dispatcher.Invoke(() =>
            {
                _robotBanner?.Hide();
                StopRobotJobButton.IsEnabled = false;
                RobotStatusText.Text = $"Trabalho {job.Id}: {Describe(job.State)}.";
                _trayIcon.Notify(TrayIcon.ProductName, RobotStatusText.Text);
            });
            loop.ConnectionProblem += message => Dispatcher.Invoke(() => RobotStatusText.Text = message);
            await Task.Run(() => loop.RunRoundAsync(_robotStop.Token));
            await RefreshRobotJobsAsync();
        }
        catch (Exception exception) when (exception is PlaybookFileException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            RobotStatusText.Text = "O robô parou: " + exception.GetType().Name;
        }
        finally
        {
            _robotBanner?.Hide();
            StopRobotJobButton.IsEnabled = false;
            _robotStop.Dispose();
            _robotStop = null;
            _robotRoundRunning = false;
        }
    }

    private async Task RefreshRobotJobsAsync()
    {
        if (_jobStore is null)
        {
            return;
        }

        var jobs = await _jobStore.RecentAsync(30, CancellationToken.None);
        RobotJobsList.ItemsSource = jobs.Select(job => $"{job.UpdatedAt:dd/MM HH:mm}  {job.Id}  —  {Describe(job.State)}  —  {job.Summary}").ToArray();
    }

    private static string Describe(RobotJobState state) => state switch
    {
        RobotJobState.Queued => "na fila",
        RobotJobState.Running => "em andamento",
        RobotJobState.Completed => "concluído",
        RobotJobState.Stopped => "parado, precisa de uma pessoa",
        _ => "interrompido",
    };

    private void StopRobot()
    {
        _robotStop?.Cancel();
        _robotTimer?.Stop();
        _localApi?.Dispose();
        _robotBanner?.Close();
        _crmHttp.Dispose();
    }

    private async Task LoadRobotTabAsync()
    {
        if (_robotLoaded)
        {
            return;
        }

        _robotLoaded = true;
        await RefreshRobotJobsAsync();
        // The pharmacy's own playbooks (in the Agent's data) replace shipped ones with the same ID.
        var own = await JsonPlaybookStore.ListAsync(Path.Combine(DataDirectory, "playbooks"), CancellationToken.None);
        var shipped = await JsonPlaybookStore.ListAsync(ShippedPlaybooksDirectory, CancellationToken.None);
        var playbooks = shipped.Where(playbook => own.All(mine => mine.Id != playbook.Id)).Concat(own).ToArray();
        PlaybookCombo.ItemsSource = playbooks;
        if (playbooks.Length > 0)
        {
            PlaybookCombo.SelectedIndex = 0;
        }
        else
        {
            NoticesStatusText.Text = "Nenhum roteiro instalado.";
        }
    }

    private async void PlaybookCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaybookCombo.SelectedItem is not Playbook playbook)
        {
            return;
        }

        try
        {
            var policy = await DialogPolicies.LoadAsync(playbook.Id, CancellationToken.None);
            var seen = await DialogPolicies.SeenAsync(playbook.Id, CancellationToken.None);
            _notices = new NoticeSettingsViewModel(playbook, policy, seen);
            NoticesGrid.ItemsSource = _notices.Rows;
            SaveNoticesButton.IsEnabled = true;
            NoticesStatusText.Text = seen.Count == 0
                ? $"{_notices.Rows.Count} aviso(s)."
                : $"{_notices.Rows.Count} aviso(s), {seen.Count} encontrado(s) pelo robô.";
        }
        catch (PlaybookFileException)
        {
            NoticesStatusText.Text = "O arquivo de escolhas desta farmácia está danificado; nada foi carregado.";
            SaveNoticesButton.IsEnabled = false;
        }
    }

    private async void SaveNoticesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_notices is null || PlaybookCombo.SelectedItem is not Playbook playbook)
        {
            return;
        }

        var policy = _notices.BuildPolicy();
        var errors = PlaybookValidator.Validate(playbook.WithPolicy(policy));
        if (errors.Count > 0)
        {
            NoticesStatusText.Text = "Não salvo: " + string.Join(", ", errors.Select(error => error.Code));
            return;
        }

        await DialogPolicies.SaveAsync(policy, CancellationToken.None);
        NoticesStatusText.Text = $"Salvo: {policy.Rules.Length} escolha(s) desta farmácia.";
    }
}
