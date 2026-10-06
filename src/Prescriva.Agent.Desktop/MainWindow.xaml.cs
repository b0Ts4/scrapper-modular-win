using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Desktop.Configuration;
using Prescriva.Agent.Desktop.Inspection;
using Prescriva.Agent.Desktop.Monitoring;
using Prescriva.Agent.Desktop.Overlay;
using Prescriva.Agent.Desktop.Shell;
using Prescriva.Agent.Desktop.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Infrastructure.Configuration;
using Prescriva.Agent.Infrastructure.Diagnostics;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using Prescriva.Agent.Windows.Automation;
using Prescriva.Agent.Windows.Processes;
using Prescriva.Agent.Windows.Runtime;
using Prescriva.Agent.Windows.Startup;

namespace Prescriva.Agent.Desktop;

/// <summary>
/// The minimal, genuinely runnable host for the configurator vertical slice: enough UI to
/// create an integration, add a stage, drive a live pointer-inspection session with a
/// click-through highlight overlay, confirm a selection and assign it a semantic ID as a
/// field, save/reload the configuration, and resolve + read a saved field's live value.
///
/// Deliberately thin - all real orchestration lives in <see cref="IntegrationEditorViewModel"/>
/// (Application-facing, testable with fakes) and <see cref="InspectorViewModel"/> (Task 4);
/// this class only wires them together and reacts to button clicks and a polling timer.
///
/// Also hosts the rest of the milestone flow: test mode (<see cref="TestModeViewModel"/>,
/// against the running target application), and activation/monitoring
/// (<see cref="RuntimeMonitorViewModel"/> over the real <see cref="AgentRuntime"/>, the
/// DPAPI-protected SQLite outbox and the JSON-lines technical log). Local data lives under
/// <see cref="DataDirectory"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AutomationDispatcher _automationDispatcher = new();
    private readonly InspectionController _inspectionController;
    private readonly InspectorViewModel _inspectorViewModel;
    private readonly IntegrationEditorViewModel _editorViewModel;
    private readonly HighlightOverlayWindow _overlay = new();
    private readonly DispatcherTimer _pointerPollTimer;

    private readonly UiAutomationRuntimeFactories _runtimeFactories;
    private readonly StructuredTechnicalLog _technicalLog;
    private readonly RuntimeMonitorViewModel _monitorViewModel;
    private readonly ApprovalService _approvalService;
    private readonly JsonConfigurationStore _configurationStore;
    private readonly JsonMonitoringPreferenceStore _monitoringPreference;
    private readonly IStartupRegistration _startupRegistration;
    private bool _updatingStartupOption;
    private readonly TrayIcon _trayIcon;
    private readonly SqliteEventOutbox _outbox;
    private readonly SqliteAttachmentStore _attachments;

    private InspectionState? _confirmedSelection;
    private bool _exiting;
    private bool _hiddenNoticeShown;
    private TestModeViewModel? _testModeViewModel;

    public MainWindow()
    {
        InitializeComponent();

        var inspector = new UiAutomationElementInspector(_automationDispatcher);
        _inspectionController = new InspectionController(inspector, additionalExcludedProcessIds: [Environment.ProcessId]);
        _inspectorViewModel = new InspectorViewModel(_inspectionController, Dispatcher);
        _inspectorViewModel.PropertyChanged += (_, _) => OnInspectorStateChanged();

        var resolver = new UiAutomationSelectorResolver(_automationDispatcher);
        var captureProvider = new UiAutomationCaptureProvider(_automationDispatcher);
        var store = new JsonConfigurationStore(Path.Combine(DataDirectory, "configurations"));
        _configurationStore = store;
        _monitoringPreference = new JsonMonitoringPreferenceStore(DataDirectory);
        // Installed from the Store (MSIX) the Run key is virtualized: use the package's startup task.
        _startupRegistration = PackageIdentity.IsPackaged
            ? new PackagedStartupRegistration()
            : new RunKeyStartupRegistration(
                Environment.GetEnvironmentVariable("PRESCRIVA_AGENT_RUN_KEY") is { Length: > 0 } runKey ? runKey : RunKeyStartupRegistration.DefaultSubKey);
        _approvalService = new ApprovalService(new JsonApprovalStore(Path.Combine(DataDirectory, "approvals")));
        _editorViewModel = new IntegrationEditorViewModel(store, resolver, captureProvider);

        _runtimeFactories = new UiAutomationRuntimeFactories(_automationDispatcher);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "logs"));
        _technicalLog = new StructuredTechnicalLog(Path.Combine(DataDirectory, "logs", "technical.jsonl"));
        var protector = new DpapiPayloadProtector();
        var outbox = new SqliteEventOutbox(Path.Combine(DataDirectory, "events.db"), protector);
        var attachments = new SqliteAttachmentStore(Path.Combine(DataDirectory, "events.db"), protector);
        _outbox = outbox;
        _attachments = attachments;
        var runtime = new AgentRuntime(
            new WindowsApplicationInstanceSource(),
            _runtimeFactories.CreateTriggerProvider,
            _runtimeFactories.CreateSelectorResolver,
            _runtimeFactories.CreateCaptureProvider,
            outbox,
            _technicalLog,
            attachments: attachments);
        _monitorViewModel = new RuntimeMonitorViewModel(
            runtime,
            outbox,
            Dispatcher,
            CreateCapacityPolicy(),
            async cancellationToken =>
            {
                await outbox.DeleteAllAsync(cancellationToken);
                await attachments.DeleteAllAsync(cancellationToken);
                _technicalLog.Clear();
            },
            attachments);
        _monitorViewModel.PropertyChanged += (_, _) => RefreshMonitor();
        Loaded += async (_, _) => await ApplyRetentionAsync(outbox, attachments);

        _trayIcon = new TrayIcon(ShowFromTray, () => StopMonitoringButton_Click(this, new RoutedEventArgs()), ExitApplication);
        StartWithWindowsCheckBox.Checked += async (_, _) => await SetStartWithWindowsAsync(true);
        StartWithWindowsCheckBox.Unchecked += async (_, _) => await SetStartWithWindowsAsync(false);
        Loaded += async (_, _) => await ShowStartupStateAsync();
        Closing += OnClosing;

        _pointerPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _pointerPollTimer.Tick += async (_, _) => await PollPointerAsync();

        Closed += async (_, _) =>
        {
            // Exiting keeps the integration "left active", so starting with Windows resumes it;
            // only the Stop button clears that choice.
            await _monitorViewModel.StopAsync();
            _trayIcon.Dispose();
            _technicalLog.Dispose();
            _pointerPollTimer.Stop();
            _overlay.Close();
            _inspectorViewModel.Dispose();
            _automationDispatcher.Dispose();
            System.Windows.Application.Current?.Shutdown();
        };
    }

    /// <summary>
    /// Started by "Iniciar com o Windows": the window stays hidden (the tray icon shows the
    /// Agent is running) and the integration the operator left active is resumed - only if
    /// its approval still matches its content. Any other outcome is shown, never activated.
    /// </summary>
    public async Task StartInBackgroundAsync()
    {
        await ApplyRetentionAsync(_outbox, _attachments);
        StartupDecision decision;
        try
        {
            decision = await new StartupActivation(_monitoringPreference, _configurationStore, _approvalService).DecideAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            decision = new StartupDecision(StartupDecisionKind.ConfigurationUnavailable, ex.GetType().Name);
        }

        if (decision.Kind == StartupDecisionKind.Activate)
        {
            try
            {
                IntegrationIdBox.Text = decision.ConfigurationId;
                await _editorViewModel.ReloadAsync(decision.ConfigurationId!);
                await _monitorViewModel.StartAsync(decision.Configuration!, decision.Approval);
                SetStatus(decision.Describe());
                _trayIcon.Notify(TrayIcon.ProductName, decision.Describe());
            }
            catch (Exception ex)
            {
                // Never fail silently at sign-in: the tray stays, with the reason.
                var message = $"Iniciado com o Windows: não foi possível retomar '{decision.ConfigurationId}' ({ex.GetType().Name}); nada sendo monitorado.";
                SetStatus(message);
                _trayIcon.Notify(TrayIcon.ProductName, message, warning: true);
            }
        }
        else
        {
            SetStatus(decision.Describe());
            _trayIcon.Notify(TrayIcon.ProductName, decision.Describe(), warning: decision.Kind != StartupDecisionKind.NothingActive);
        }

        RefreshTrayStatus();
    }

    /// <summary>Shows the window (tray menu, double click, or a second start of the Agent).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || !_monitorViewModel.IsMonitoring)
        {
            return;
        }

        // Closing the window must not silently stop monitoring: keep running in the tray.
        e.Cancel = true;
        Hide();
        if (!_hiddenNoticeShown)
        {
            _hiddenNoticeShown = true;
            _trayIcon.Notify(TrayIcon.ProductName, "Continua monitorando na bandeja. Use o ícone para abrir, parar ou sair.");
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private async Task ShowStartupStateAsync()
    {
        try
        {
            ShowStartupState(await _startupRegistration.GetStateAsync(), announce: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Estado de 'Iniciar com o Windows' indisponível ({ex.GetType().Name}).");
        }
    }

    private async Task SetStartWithWindowsAsync(bool enabled)
    {
        if (_updatingStartupOption)
        {
            return;
        }

        try
        {
            var state = enabled
                ? await _startupRegistration.EnableAsync(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unknown."))
                : await _startupRegistration.DisableAsync();
            ShowStartupState(state, announce: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível alterar 'Iniciar com o Windows': {ex.Message}");
        }
    }

    /// <summary>Shows the real state (which Windows may override) on the checkbox, with the reason when it differs.</summary>
    private void ShowStartupState(StartupRegistrationState state, bool announce)
    {
        var enabled = state is StartupRegistrationState.Enabled or StartupRegistrationState.EnabledByPolicy;
        _updatingStartupOption = true;
        try
        {
            StartWithWindowsCheckBox.IsChecked = enabled;
            StartWithWindowsCheckBox.IsEnabled = state is not (StartupRegistrationState.DisabledByPolicy or StartupRegistrationState.EnabledByPolicy);
        }
        finally
        {
            _updatingStartupOption = false;
        }

        var message = state switch
        {
            StartupRegistrationState.Enabled => "Iniciar com o Windows: ativado. O Agent abrirá na bandeja ao entrar no Windows e retomará a integração ativa, se aprovada.",
            StartupRegistrationState.DisabledByUser => "Iniciar com o Windows está desativado no Gerenciador de Tarefas (Aplicativos de inicialização). Ative o Receita Fácil Agent lá para que ele inicie com o Windows.",
            StartupRegistrationState.DisabledByPolicy => "Iniciar com o Windows foi bloqueado por uma política do administrador.",
            StartupRegistrationState.EnabledByPolicy => "Iniciar com o Windows foi ativado por uma política do administrador.",
            _ => "Iniciar com o Windows: desativado.",
        };

        if (announce || state is not (StartupRegistrationState.Enabled or StartupRegistrationState.Disabled))
        {
            SetStatus(message);
        }
    }

    private void RefreshTrayStatus() =>
        _trayIcon.SetStatus(_monitorViewModel.IsMonitoring
            ? $"{TrayIcon.ProductName} - monitorando '{_editorViewModel.Configuration?.Name}'"
            : $"{TrayIcon.ProductName} - parado");

    /// <summary>
    /// Where configurations, the event queue and technical logs are kept:
    /// %LOCALAPPDATA%\Prescriva\Agent, or the directory named by the
    /// PRESCRIVA_AGENT_DATA environment variable (used to isolate automated UI tests).
    /// </summary>
    internal static string DataDirectory =>
        Environment.GetEnvironmentVariable("PRESCRIVA_AGENT_DATA") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prescriva", "Agent");

    private void CreateIntegrationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var application = new ApplicationDefinition(ProcessIdentityBox.Text.Trim(), WindowRuleBox.Text.Trim());
            _editorViewModel.CreateIntegration(IntegrationIdBox.Text.Trim(), IntegrationNameBox.Text.Trim(), application);
            SetStatus($"Integração '{_editorViewModel.Configuration!.Id}' criada. {UnsavedChanges()}");
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível criar a integração: {ex.Message}");
        }
    }

    private void AddStageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _editorViewModel.AddStage(StageIdBox.Text.Trim(), StageNameBox.Text.Trim());
            SetStatus($"Etapa '{StageIdBox.Text.Trim()}' adicionada.");
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível adicionar a etapa: {ex.Message}");
        }
    }

    private async void StartInspectionButton_Click(object sender, RoutedEventArgs e)
    {
        await _inspectorViewModel.StartAsync();
        _pointerPollTimer.Start();
        SetStatus("Seleção iniciada: passe o mouse sobre o campo ou botão na aplicação.");
    }

    private async void StopInspectionButton_Click(object sender, RoutedEventArgs e)
    {
        _pointerPollTimer.Stop();
        await _inspectorViewModel.StopAsync();
        _overlay.UpdateHighlight(null);
        SetStatus("Seleção parada.");
    }

    private async void ConfirmSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirmed = await _inspectorViewModel.ConfirmAsync();
            _confirmedSelection = confirmed;
            ConfirmedSelectionText.Text =
                $"Confirmado: AutomationId='{confirmed.Snapshot?.AutomationId}', Nome='{confirmed.Snapshot?.Name}', Tipo='{confirmed.Snapshot?.ControlType}', Rótulo='{LabelOf(confirmed.Snapshot)}'.";
            SetStatus("Seleção confirmada. Dê um ID e clique em 'Adicionar campo selecionado' ou 'Adicionar gatilho selecionado'.");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus($"Não foi possível confirmar: {ex.Message}");
        }
    }

    private void AddFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmedSelection?.Fingerprint is not { } fingerprint)
        {
            SetStatus("Confirme uma seleção antes de adicionar um campo.");
            return;
        }

        try
        {
            _editorViewModel.AddField(
                FieldSemanticIdBox.Text.Trim(),
                StageIdBox.Text.Trim(),
                FieldMeaningBox.Text.Trim(),
                FieldRequiredBox.IsChecked == true,
                fingerprint,
                FieldKindBox.SelectedIndex switch
                {
                    1 => FieldKind.File,
                    2 => FieldKind.OcrText,
                    _ => FieldKind.Text,
                });
            SetStatus($"Campo '{FieldSemanticIdBox.Text.Trim()}' adicionado. {UnsavedChanges()}");
        }
        catch (ArgumentException ex)
        {
            SetStatus($"ID inválido: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus($"Não foi possível adicionar o campo: {ex.Message}");
        }
    }

    private void AddTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmedSelection?.Fingerprint is not { } fingerprint)
        {
            SetStatus("Confirme uma seleção antes de adicionar um gatilho.");
            return;
        }

        try
        {
            var actions = TriggerActionsBuilder.Build(
                TriggerCaptureFieldsBox.Text,
                TriggerEmitEventBox.Text,
                TriggerTransitionStageBox.Text,
                TriggerClearStateBox.IsChecked == true,
                (TriggerTerminalAction)Math.Max(0, TriggerTerminalBox.SelectedIndex));
            _editorViewModel.AddTrigger(
                TriggerSemanticIdBox.Text.Trim(),
                StageIdBox.Text.Trim(),
                fingerprint,
                TriggerObservedEventBox.Text.Trim(),
                actions);
            SetStatus($"Gatilho '{TriggerSemanticIdBox.Text.Trim()}' adicionado com {actions.Length} ação(ões). {UnsavedChanges()}");
        }
        catch (ArgumentException ex)
        {
            SetStatus($"Gatilho inválido: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus($"Não foi possível adicionar o gatilho: {ex.Message}");
        }
    }

    private void RemoveStageButton_Click(object sender, RoutedEventArgs e) =>
        Edit(() => _editorViewModel.RemoveStage(StageIdBox.Text.Trim()), $"Etapa '{StageIdBox.Text.Trim()}' removida.");

    private void UpdateFieldButton_Click(object sender, RoutedEventArgs e) =>
        Edit(
            () => _editorViewModel.UpdateField(FieldSemanticIdBox.Text.Trim(), FieldMeaningBox.Text.Trim(), FieldRequiredBox.IsChecked == true),
            $"Campo '{FieldSemanticIdBox.Text.Trim()}' atualizado.");

    private void RemoveFieldButton_Click(object sender, RoutedEventArgs e) =>
        Edit(() => _editorViewModel.RemoveField(FieldSemanticIdBox.Text.Trim()), $"Campo '{FieldSemanticIdBox.Text.Trim()}' removido.");

    private void ReplaceTriggerActionsButton_Click(object sender, RoutedEventArgs e) =>
        Edit(
            () => _editorViewModel.ReplaceTriggerActions(
                TriggerSemanticIdBox.Text.Trim(),
                TriggerActionsBuilder.Build(
                    TriggerCaptureFieldsBox.Text,
                    TriggerEmitEventBox.Text,
                    TriggerTransitionStageBox.Text,
                    TriggerClearStateBox.IsChecked == true,
                    (TriggerTerminalAction)Math.Max(0, TriggerTerminalBox.SelectedIndex))),
            $"Ações do gatilho '{TriggerSemanticIdBox.Text.Trim()}' substituídas.");

    private void RemoveTriggerButton_Click(object sender, RoutedEventArgs e) =>
        Edit(() => _editorViewModel.RemoveTrigger(TriggerSemanticIdBox.Text.Trim()), $"Gatilho '{TriggerSemanticIdBox.Text.Trim()}' removido.");

    /// <summary>Applies one editor operation, reporting a refusal instead of applying part of it.</summary>
    private void Edit(Action edit, string success)
    {
        try
        {
            edit();
            SetStatus($"{success} {UnsavedChanges()}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            SetStatus($"Edição recusada: {ex.Message}");
        }
    }

    /// <summary>Shows whether the current configuration content may be activated.</summary>
    private async Task RefreshApprovalStateAsync()
    {
        if (_editorViewModel.Configuration is not { } configuration)
        {
            ApprovalStateText.Text = "(nenhuma integração)";
            return;
        }

        try
        {
            var status = await _approvalService.GetStatusAsync(configuration);
            ApprovalStateText.Text = status.State switch
            {
                ApprovalState.Approved => $"Aprovada em {status.LastApprovedAtUtc!.Value.ToLocalTime():g}: pode ser ativada.",
                ApprovalState.ChangedSinceTest => $"Alterada desde o último teste aprovado ({status.LastApprovedAtUtc!.Value.ToLocalTime():g}): teste e aprove novamente para ativar.",
                _ => "Não testada: execute o modo de teste e aprove antes de ativar.",
            };
            ApprovalStateText.Foreground = status.State == ApprovalState.Approved
                ? System.Windows.Media.Brushes.DarkGreen
                : System.Windows.Media.Brushes.DarkOrange;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            ApprovalStateText.Text = $"Estado de aprovação indisponível ({ex.GetType().Name}).";
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _editorViewModel.SaveAsync();
            SetStatus($"Salvo. {UnsavedChanges()}");
        }
        catch (ConfigurationValidationException ex)
        {
            SetStatus($"Não foi salvo: a configuração é inválida ({string.Join("; ", ex.Errors.Select(err => err.Code))}).");
        }
        catch (Exception ex)
        {
            SetStatus($"Falha ao salvar: {ex.Message}");
        }
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _editorViewModel.ReloadAsync(IntegrationIdBox.Text.Trim());
            SetStatus($"Integração '{_editorViewModel.Configuration!.Id}' recarregada com {_editorViewModel.Configuration.Fields.Length} campo(s).");
        }
        catch (Exception ex)
        {
            SetStatus($"Falha ao recarregar: {ex.Message}");
        }
    }

    private async void ResolveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var resolution = await _editorViewModel.ResolveFieldAsync(ResolveFieldIdBox.Text.Trim());
            SetStatus($"Localização: {resolution.Status} (confiança {resolution.Confidence:P0}).");
        }
        catch (Exception ex)
        {
            SetStatus($"Falha ao localizar: {ex.Message}");
        }
    }

    private async void ReadValueButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _editorViewModel.ReadFieldValueAsync(ResolveFieldIdBox.Text.Trim());
            SetStatus($"Leitura: resultado={result.Outcome}, valor='{result.Value}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Falha na leitura: {ex.Message}");
        }
    }

    private void PrepareTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editorViewModel.Configuration is not { } configuration)
        {
            SetStatus("Crie ou recarregue uma integração antes de testá-la.");
            return;
        }

        var processId = FindRunningProcessId(configuration.Application);
        if (processId is null)
        {
            SetStatus($"Aplicação '{configuration.Application.ProcessIdentity}' com janela '{configuration.Application.WindowRule}' não está em execução. Abra-a e tente novamente.");
            return;
        }

        var instance = new ApplicationInstance(Guid.NewGuid(), processId.Value);
        var runner = new IntegrationTestRunner(
            _runtimeFactories.CreateSelectorResolver(instance),
            _runtimeFactories.CreateCaptureProvider(instance),
            _runtimeFactories.CreateTriggerProvider(instance, Guid.NewGuid()),
            triggerTimeout: TimeSpan.FromSeconds(15));
        _testModeViewModel = new TestModeViewModel(runner, Dispatcher, approvals: _approvalService);
        TestModeHost.Attach(_testModeViewModel, configuration);
        TestModeHost.Approved -= OnTestApproved;
        TestModeHost.Approved += OnTestApproved;
        TestModeHost.Visibility = Visibility.Visible;
        SetStatus($"Teste preparado para o processo {processId}. Clique em 'Executar teste' e, na aplicação, pressione cada botão configurado, na ordem, em até 15 segundos cada.");
    }

    private async void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editorViewModel.Configuration is not { } configuration)
        {
            SetStatus("Crie ou recarregue uma integração antes de ativá-la.");
            return;
        }

        try
        {
            // The stored approval for exactly this content, if any; AgentRuntime enforces the gate.
            var approval = (await _approvalService.GetStatusAsync(configuration)).Approval;
            var status = await _monitorViewModel.StartAsync(configuration, approval);
            if (status == ActivationStatus.Activated)
            {
                await _monitoringPreference.SetActiveConfigurationIdAsync(configuration.Id, CancellationToken.None);
            }
            else if (_testModeViewModel is not null)
            {
                _testModeViewModel.GateMessage = _monitorViewModel.StatusText;
            }
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void OnTestApproved(object? sender, EventArgs e) => await RefreshApprovalStateAsync();

    private async void StopMonitoringButton_Click(object sender, RoutedEventArgs e)
    {
        await _monitorViewModel.StopAsync();
        await _monitoringPreference.SetActiveConfigurationIdAsync(null, CancellationToken.None);
    }

    private async void ClearLocalDataButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Apagar TODOS os eventos da fila local (inclusive os ainda não confirmados) e o log técnico? Esta ação não pode ser desfeita. As configurações são mantidas.",
            "Limpar dados locais",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _monitorViewModel.ClearLocalDataAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Falha ao limpar os dados locais: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Pending-event alert thresholds: 1000 (warning) and 5000 (critical) by default, or the
    /// PRESCRIVA_AGENT_OUTBOX_WARNING / PRESCRIVA_AGENT_OUTBOX_CRITICAL environment variables.
    /// </summary>
    private static OutboxCapacityPolicy CreateCapacityPolicy()
    {
        static int Read(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;

        var warning = Read("PRESCRIVA_AGENT_OUTBOX_WARNING", 1000);
        var critical = Math.Max(warning, Read("PRESCRIVA_AGENT_OUTBOX_CRITICAL", 5000));
        return new OutboxCapacityPolicy(warning, critical);
    }

    private void RefreshMonitor()
    {
        MonitorStatusText.Text = _monitorViewModel.StatusText;
        var diagnostics = _monitorViewModel.Diagnostics;
        DiagnosticsList.ItemsSource = diagnostics;
        EventsList.ItemsSource = _monitorViewModel.Events;
        if (diagnostics.Count > 0)
        {
            DiagnosticsList.ScrollIntoView(diagnostics[^1]);
        }

        ActivateButton.IsEnabled = !_monitorViewModel.IsMonitoring;
        RefreshTrayStatus();
        StopMonitoringButton.IsEnabled = _monitorViewModel.IsMonitoring;
        ClearLocalDataButton.IsEnabled = !_monitorViewModel.IsMonitoring;

        HealthText.Text = _monitorViewModel.HealthText;
        HealthText.Foreground = _monitorViewModel.HealthState switch
        {
            IntegrationHealthState.Broken => System.Windows.Media.Brushes.DarkRed,
            IntegrationHealthState.Degraded => System.Windows.Media.Brushes.DarkOrange,
            _ => System.Windows.Media.Brushes.DarkGreen,
        };

        var alert = _monitorViewModel.CapacityAlert;
        CapacityAlertText.Text = alert ?? string.Empty;
        CapacityAlertText.Foreground = _monitorViewModel.IsCapacityCritical ? System.Windows.Media.Brushes.DarkRed : System.Windows.Media.Brushes.DarkOrange;
        CapacityAlertText.Visibility = alert is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ApplyRetentionAsync(SqliteEventOutbox outbox, SqliteAttachmentStore attachments)
    {
        try
        {
            await new RetentionService(outbox).ApplyAsync(System.Threading.CancellationToken.None);
            await LocalDataMaintenance.CollectAttachmentGarbageAsync(outbox, attachments, System.Threading.CancellationToken.None);
            await _monitorViewModel.RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Fila local de eventos indisponível: {ex.GetType().Name}.");
        }
    }

    private static int? FindRunningProcessId(ApplicationDefinition application)
    {
        foreach (var process in Process.GetProcessesByName(ProcessIdentity.ToProcessName(application.ProcessIdentity)))
        {
            using (process)
            {
                if (string.IsNullOrWhiteSpace(application.WindowRule) ||
                    string.Equals(process.MainWindowTitle, application.WindowRule, StringComparison.OrdinalIgnoreCase))
                {
                    return process.Id;
                }
            }
        }

        return null;
    }

    private string UnsavedChanges() =>
        _editorViewModel.HasUnsavedChanges ? "Alterações não salvas: sim." : "Alterações não salvas: não.";

    private void RefreshConfigurationSummary()
    {
        if (_editorViewModel.Configuration is not { } configuration)
        {
            ConfigurationSummaryText.Text = "(nenhuma integração)";
            return;
        }

        var stages = string.Join(", ", configuration.Stages.Select(stage => stage.Id));
        var fields = string.Join(", ", configuration.Fields.Select(field => $"{field.Id}@{field.StageId}{(field.Required ? "*" : "")}{field.Kind switch { FieldKind.File => "[file]", FieldKind.OcrText => "[ocr]", _ => "" }}"));
        var triggers = string.Join(" | ", configuration.Triggers.Select(trigger =>
            $"{trigger.Id}@{trigger.StageId}: {string.Join(" > ", trigger.Actions.Select(DescribeAction))}"));
        ConfigurationSummaryText.Text =
            $"'{configuration.Id}' → {configuration.Application.ProcessIdentity}\nEtapas: {stages}\nCampos: {fields}\nGatilhos: {triggers}";
    }

    private static string DescribeAction(TriggerActionDefinition action) => action switch
    {
        CaptureFieldsAction capture => $"capture({string.Join(",", capture.FieldIds)})",
        EmitEventAction emit => $"emit({emit.EventType})",
        TransitionStageAction transition => $"goto({transition.StageId})",
        ClearStateAction => "clear",
        FinishSessionAction => "finish",
        CancelSessionAction => "cancel",
        _ => action.GetType().Name,
    };

    private async System.Threading.Tasks.Task PollPointerAsync()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        await _inspectorViewModel.ObservePointerAsync(new ScreenPoint(point.X, point.Y));
    }

    private void OnInspectorStateChanged()
    {
        _overlay.UpdateHighlight(_inspectorViewModel.Bounds);

        var snapshot = _inspectorViewModel.Snapshot;
        HoverStateText.Text = snapshot is not null
            ? $"Sob o cursor: AutomationId='{snapshot.AutomationId}', Nome='{snapshot.Name}', Tipo='{snapshot.ControlType}', Rótulo='{LabelOf(snapshot)}'."
            : _inspectorViewModel.Warnings.Length > 0
                ? string.Join(" ", _inspectorViewModel.Warnings)
                : "(nada sob o cursor)";
    }

    private static string LabelOf(ElementSnapshot? snapshot) =>
        snapshot is null || snapshot.NearbyLabels.IsDefaultOrEmpty ? string.Empty : snapshot.NearbyLabels[0];

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        RefreshConfigurationSummary();
        _ = RefreshApprovalStateAsync();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct point);
}
