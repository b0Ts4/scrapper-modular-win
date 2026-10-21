using System.IO;
using System.Windows;
using System.Windows.Controls;
using Prescriva.Agent.Desktop.Playbooks;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Playbooks;

namespace Prescriva.Agent.Desktop;

/// <summary>Step 6, the robot (milestone 2): the playbooks shipped with the Agent and each pharmacy's choices for their notices.</summary>
public partial class MainWindow
{
    /// <summary>Playbooks shipped with the Agent: read-only JSON next to the executable.</summary>
    internal static string ShippedPlaybooksDirectory => Path.Combine(AppContext.BaseDirectory, "Playbooks");

    /// <summary>The pharmacy's notice choices and the notices the robot met, per playbook.</summary>
    internal static JsonDialogPolicyStore DialogPolicies { get; } = new(Path.Combine(DataDirectory, "playbooks"));

    private NoticeSettingsViewModel? _notices;
    private bool _robotLoaded;

    private async Task LoadRobotTabAsync()
    {
        if (_robotLoaded)
        {
            return;
        }

        _robotLoaded = true;
        var playbooks = await JsonPlaybookStore.ListAsync(ShippedPlaybooksDirectory, CancellationToken.None);
        PlaybookCombo.ItemsSource = playbooks;
        if (playbooks.Count > 0)
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
