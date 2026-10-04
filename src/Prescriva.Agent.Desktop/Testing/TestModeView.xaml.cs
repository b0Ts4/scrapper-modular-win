using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Desktop.Testing;

/// <summary>
/// The minimal, genuinely runnable host for the test-mode vertical slice: runs
/// <see cref="TestModeViewModel.RunAsync"/> for a supplied configuration, renders its
/// field/trigger results (including each failure's actionable Portuguese text), and lets
/// the operator approve the configuration once every check has passed.
///
/// Deliberately thin - all real orchestration lives in <see cref="TestModeViewModel"/>
/// (Application-facing, testable with fakes); this class only wires it to the view and
/// reacts to button clicks, the same division <c>MainWindow</c> already established for
/// <c>IntegrationEditorViewModel</c>/<c>InspectorViewModel</c>.
/// </summary>
public partial class TestModeView : UserControl
{
    private TestModeViewModel? _viewModel;
    private IntegrationConfiguration? _configuration;

    public TestModeView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wires this view to <paramref name="viewModel"/> and the configuration it will test.
    /// Replaces any previously wired view model, unsubscribing from it first.
    /// </summary>
    public void Attach(TestModeViewModel viewModel, IntegrationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(configuration);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        _configuration = configuration;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        TestStatusText.Text = "Pronto.";

        Refresh();
    }

    private async void RunTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _configuration is null)
        {
            return;
        }

        TestStatusText.Text = "Executando teste...";
        try
        {
            await _viewModel.RunAsync(_configuration);
            TestStatusText.Text = _viewModel.CanApprove
                ? "Teste concluído: todos os campos e gatilhos passaram."
                : "Teste concluído: um ou mais campos ou gatilhos falharam. Veja os detalhes acima.";
        }
        catch (Exception ex)
        {
            TestStatusText.Text = $"Falha ao executar o teste: {ex.Message}";
        }
    }

    private void ApproveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            _viewModel.Approve();
            TestStatusText.Text = "Configuração aprovada. Ela pode ser ativada agora.";
        }
        catch (InvalidOperationException ex)
        {
            TestStatusText.Text = $"Não foi possível aprovar: {ex.Message}";
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_viewModel is null)
        {
            return;
        }

        FieldResultsList.ItemsSource = _viewModel.FieldResults;
        TriggerResultsList.ItemsSource = _viewModel.TriggerResults;
        ApproveButton.IsEnabled = _viewModel.CanApprove;

        if (!string.IsNullOrEmpty(_viewModel.GateMessage))
        {
            GateMessageText.Text = _viewModel.GateMessage;
            GateMessageText.Visibility = Visibility.Visible;
        }
        else
        {
            GateMessageText.Visibility = Visibility.Collapsed;
        }
    }
}
