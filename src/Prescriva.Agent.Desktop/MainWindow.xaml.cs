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
using Prescriva.Agent.Desktop.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Infrastructure.Configuration;
using Prescriva.Agent.Infrastructure.Diagnostics;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using Prescriva.Agent.Windows.Automation;
using Prescriva.Agent.Windows.Processes;
using Prescriva.Agent.Windows.Runtime;

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

    private InspectionState? _confirmedSelection;
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
        _editorViewModel = new IntegrationEditorViewModel(store, resolver, captureProvider);

        _runtimeFactories = new UiAutomationRuntimeFactories(_automationDispatcher);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "logs"));
        _technicalLog = new StructuredTechnicalLog(Path.Combine(DataDirectory, "logs", "technical.jsonl"));
        var outbox = new SqliteEventOutbox(Path.Combine(DataDirectory, "events.db"), new DpapiPayloadProtector());
        var runtime = new AgentRuntime(
            new WindowsApplicationInstanceSource(),
            _runtimeFactories.CreateTriggerProvider,
            _runtimeFactories.CreateSelectorResolver,
            _runtimeFactories.CreateCaptureProvider,
            outbox,
            _technicalLog);
        _monitorViewModel = new RuntimeMonitorViewModel(runtime, outbox, Dispatcher);
        _monitorViewModel.PropertyChanged += (_, _) => RefreshMonitor();
        Loaded += async (_, _) => await ApplyRetentionAsync(outbox);

        _pointerPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _pointerPollTimer.Tick += async (_, _) => await PollPointerAsync();

        Closed += async (_, _) =>
        {
            await _monitorViewModel.StopAsync();
            _technicalLog.Dispose();
            _pointerPollTimer.Stop();
            _overlay.Close();
            _inspectorViewModel.Dispose();
            _automationDispatcher.Dispose();
        };
    }

    /// <summary>
    /// Where configurations, the event queue and technical logs are kept:
    /// %LOCALAPPDATA%\Prescriva\Agent, or the directory named by the
    /// PRESCRIVA_AGENT_DATA environment variable (used to isolate automated UI tests).
    /// </summary>
    private static string DataDirectory =>
        Environment.GetEnvironmentVariable("PRESCRIVA_AGENT_DATA") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prescriva", "Agent");

    private void CreateIntegrationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var application = new ApplicationDefinition(ProcessIdentityBox.Text.Trim(), WindowRuleBox.Text.Trim());
            _editorViewModel.CreateIntegration(IntegrationIdBox.Text.Trim(), IntegrationNameBox.Text.Trim(), application);
            SetStatus($"Created integration '{_editorViewModel.Configuration!.Id}'. Unsaved changes: {_editorViewModel.HasUnsavedChanges}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create integration: {ex.Message}");
        }
    }

    private void AddStageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _editorViewModel.AddStage(StageIdBox.Text.Trim(), StageNameBox.Text.Trim());
            SetStatus($"Added stage '{StageIdBox.Text.Trim()}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to add stage: {ex.Message}");
        }
    }

    private async void StartInspectionButton_Click(object sender, RoutedEventArgs e)
    {
        await _inspectorViewModel.StartAsync();
        _pointerPollTimer.Start();
        SetStatus("Inspection started - move the mouse over the target application.");
    }

    private async void StopInspectionButton_Click(object sender, RoutedEventArgs e)
    {
        _pointerPollTimer.Stop();
        await _inspectorViewModel.StopAsync();
        _overlay.UpdateHighlight(null);
        SetStatus("Inspection stopped.");
    }

    private async void ConfirmSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirmed = await _inspectorViewModel.ConfirmAsync();
            _confirmedSelection = confirmed;
            ConfirmedSelectionText.Text =
                $"Confirmed: AutomationId='{confirmed.Snapshot?.AutomationId}', Name='{confirmed.Snapshot?.Name}', ControlType='{confirmed.Snapshot?.ControlType}'.";
            SetStatus("Selection confirmed. Assign a semantic ID and click 'Add Field'.");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus($"Cannot confirm: {ex.Message}");
        }
    }

    private void AddFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmedSelection?.Fingerprint is not { } fingerprint)
        {
            SetStatus("Confirm a selection before adding a field.");
            return;
        }

        try
        {
            _editorViewModel.AddField(
                FieldSemanticIdBox.Text.Trim(),
                StageIdBox.Text.Trim(),
                FieldMeaningBox.Text.Trim(),
                FieldRequiredBox.IsChecked == true,
                fingerprint);
            SetStatus($"Added field '{FieldSemanticIdBox.Text.Trim()}'. Unsaved changes: {_editorViewModel.HasUnsavedChanges}.");
        }
        catch (ArgumentException ex)
        {
            SetStatus($"Invalid semantic ID: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus($"Cannot add field: {ex.Message}");
        }
    }

    private void AddTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmedSelection?.Fingerprint is not { } fingerprint)
        {
            SetStatus("Confirm a selection before adding a trigger.");
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
            SetStatus($"Added trigger '{TriggerSemanticIdBox.Text.Trim()}' with {actions.Length} action(s). Unsaved changes: {_editorViewModel.HasUnsavedChanges}.");
        }
        catch (ArgumentException ex)
        {
            SetStatus($"Invalid trigger: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus($"Cannot add trigger: {ex.Message}");
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _editorViewModel.SaveAsync();
            SetStatus($"Saved. Unsaved changes: {_editorViewModel.HasUnsavedChanges}.");
        }
        catch (ConfigurationValidationException ex)
        {
            SetStatus($"Save rejected - configuration invalid: {string.Join("; ", ex.Errors.Select(err => err.Code))}");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _editorViewModel.ReloadAsync(IntegrationIdBox.Text.Trim());
            SetStatus($"Reloaded '{_editorViewModel.Configuration!.Id}' with {_editorViewModel.Configuration.Fields.Length} field(s).");
        }
        catch (Exception ex)
        {
            SetStatus($"Reload failed: {ex.Message}");
        }
    }

    private async void ResolveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var resolution = await _editorViewModel.ResolveFieldAsync(ResolveFieldIdBox.Text.Trim());
            SetStatus($"Resolve status: {resolution.Status} (confidence {resolution.Confidence:P0}).");
        }
        catch (Exception ex)
        {
            SetStatus($"Resolve failed: {ex.Message}");
        }
    }

    private async void ReadValueButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _editorViewModel.ReadFieldValueAsync(ResolveFieldIdBox.Text.Trim());
            SetStatus($"Read value: outcome={result.Outcome}, value='{result.Value}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Read failed: {ex.Message}");
        }
    }

    private void PrepareTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editorViewModel.Configuration is not { } configuration)
        {
            SetStatus("Create or reload an integration before testing it.");
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
        _testModeViewModel = new TestModeViewModel(runner, Dispatcher);
        TestModeHost.Attach(_testModeViewModel, configuration);
        TestModeHost.Visibility = Visibility.Visible;
        SetStatus($"Test prepared for process {processId}. Click 'Executar teste', then press each configured button in the application, in order, within 15 seconds each.");
    }

    private async void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editorViewModel.Configuration is not { } configuration)
        {
            SetStatus("Create or reload an integration before activating it.");
            return;
        }

        try
        {
            var status = await _monitorViewModel.StartAsync(configuration, _testModeViewModel?.LastApproval);
            if (status == ActivationStatus.NotTested && _testModeViewModel is not null)
            {
                _testModeViewModel.GateMessage = _monitorViewModel.StatusText;
            }
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void StopMonitoringButton_Click(object sender, RoutedEventArgs e) =>
        await _monitorViewModel.StopAsync();

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
        StopMonitoringButton.IsEnabled = _monitorViewModel.IsMonitoring;
    }

    private async Task ApplyRetentionAsync(SqliteEventOutbox outbox)
    {
        try
        {
            await new RetentionService(outbox).ApplyAsync(System.Threading.CancellationToken.None);
            await _monitorViewModel.RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Local event queue unavailable: {ex.GetType().Name}.");
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

    private void RefreshConfigurationSummary()
    {
        if (_editorViewModel.Configuration is not { } configuration)
        {
            ConfigurationSummaryText.Text = "(no integration)";
            return;
        }

        var stages = string.Join(", ", configuration.Stages.Select(stage => stage.Id));
        var fields = string.Join(", ", configuration.Fields.Select(field => $"{field.Id}@{field.StageId}{(field.Required ? "*" : "")}"));
        var triggers = string.Join(" | ", configuration.Triggers.Select(trigger =>
            $"{trigger.Id}@{trigger.StageId}: {string.Join(" > ", trigger.Actions.Select(DescribeAction))}"));
        ConfigurationSummaryText.Text =
            $"'{configuration.Id}' → {configuration.Application.ProcessIdentity}\nStages: {stages}\nFields: {fields}\nTriggers: {triggers}";
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
            ? $"Hovering: AutomationId='{snapshot.AutomationId}', Name='{snapshot.Name}', ControlType='{snapshot.ControlType}'."
            : _inspectorViewModel.Warnings.Length > 0
                ? string.Join(" ", _inspectorViewModel.Warnings)
                : "(nothing under the pointer)";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        RefreshConfigurationSummary();
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
