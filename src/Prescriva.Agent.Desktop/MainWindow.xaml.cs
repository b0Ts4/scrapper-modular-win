using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Desktop.Configuration;
using Prescriva.Agent.Desktop.Inspection;
using Prescriva.Agent.Desktop.Overlay;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Infrastructure.Configuration;
using Prescriva.Agent.Windows.Automation;

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
/// </summary>
public partial class MainWindow : Window
{
    private readonly AutomationDispatcher _automationDispatcher = new();
    private readonly InspectionController _inspectionController;
    private readonly InspectorViewModel _inspectorViewModel;
    private readonly IntegrationEditorViewModel _editorViewModel;
    private readonly HighlightOverlayWindow _overlay = new();
    private readonly DispatcherTimer _pointerPollTimer;

    private InspectionState? _confirmedSelection;

    public MainWindow()
    {
        InitializeComponent();

        var inspector = new UiAutomationElementInspector(_automationDispatcher);
        _inspectionController = new InspectionController(inspector, additionalExcludedProcessIds: [Environment.ProcessId]);
        _inspectorViewModel = new InspectorViewModel(_inspectionController, Dispatcher);
        _inspectorViewModel.PropertyChanged += (_, _) => OnInspectorStateChanged();

        var resolver = new UiAutomationSelectorResolver(_automationDispatcher);
        var captureProvider = new UiAutomationCaptureProvider(_automationDispatcher);
        var store = new JsonConfigurationStore(ConfigurationDirectory);
        _editorViewModel = new IntegrationEditorViewModel(store, resolver, captureProvider);

        _pointerPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _pointerPollTimer.Tick += async (_, _) => await PollPointerAsync();

        Closed += (_, _) =>
        {
            _pointerPollTimer.Stop();
            _overlay.Close();
            _inspectorViewModel.Dispose();
            _automationDispatcher.Dispose();
        };
    }

    private static string ConfigurationDirectory =>
        Path.Combine(AppContext.BaseDirectory, "configurations");

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

    private void SetStatus(string message) => StatusText.Text = message;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct point);
}
