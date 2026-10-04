using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Configuration;
using Prescriva.Agent.Infrastructure.Diagnostics;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using Prescriva.Agent.Windows.Automation;
using Prescriva.Agent.Windows.Processes;
using Prescriva.Agent.Windows.Runtime;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// The milestone's closing end-to-end flow against the real, launched
/// Prescriva.Agent.TestTarget.exe, with no fakes anywhere in the pipeline: JSON
/// configuration save/reload, UI Automation selector resolution, capture and invoke-event
/// triggers, the test-mode runner and its approval, the activation gate, the per-instance
/// <see cref="AgentRuntime"/>/<see cref="SessionCoordinator"/>, the DPAPI-protected SQLite
/// outbox and the JSON-lines technical log.
///
/// The only thing the test does "by hand" is play the human operator: it types values into
/// the TestTarget's text boxes (UIA ValuePattern) and presses its buttons (UIA
/// InvokePattern), exactly the way a real click raises the invoke event the Agent observes.
/// </summary>
public sealed class MilestoneFlowTests : IDisposable
{
    private const string ProcessName = "Prescriva.Agent.TestTarget";
    private const string WindowTitle = "Prescriva Agent Test Target";

    // Distinctive values so the "never on disk in plaintext / never in logs" checks below
    // cannot pass by accident.
    private static readonly string[] FirstMedicine = ["Dipirona-E2E-7c41", "500mg-E2E-7c41", "12-E2E-7c41"];
    private static readonly string[] SecondMedicine = ["Amoxicilina-E2E-9d02", "875mg-E2E-9d02", "21-E2E-9d02"];

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-milestone-" + Guid.NewGuid().ToString("N"));

    public MilestoneFlowTests()
    {
        Directory.CreateDirectory(_workDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    [Fact]
    public async Task Configured_tested_and_activated_integration_persists_two_items_and_budget_finished_encrypted_and_ordered()
    {
        using var target = TestTargetLauncher.Launch();
        var processId = target.Window.Current.ProcessId;

        // 1. Configure, save and reload: what runs below is the reloaded configuration.
        var store = new JsonConfigurationStore(Path.Combine(_workDirectory, "configurations"));
        var authored = BuildConfiguration();
        await store.SaveAsync(authored, CancellationToken.None);
        var configuration = await store.LoadAsync(authored.Id, CancellationToken.None);
        Assert.Equal(ConfigurationFingerprint.Compute(authored), ConfigurationFingerprint.Compute(configuration));

        using var dispatcher = new AutomationDispatcher();
        var factories = new UiAutomationRuntimeFactories(dispatcher);
        var instance = new ApplicationInstance(Guid.NewGuid(), processId);

        // 2. Test mode: every field resolves against the live window and every trigger is
        //    observed firing (the "operator" keeps pressing Add and Finish while the runner
        //    waits for them). Only a fully passing run earns an approval.
        SetMedicine(target, FirstMedicine);
        var runner = new IntegrationTestRunner(
            factories.CreateSelectorResolver(instance),
            factories.CreateCaptureProvider(instance),
            factories.CreateTriggerProvider(instance, Guid.NewGuid()),
            triggerTimeout: TimeSpan.FromSeconds(10));

        IntegrationTestReport report;
        using (var operatorCts = new CancellationTokenSource())
        {
            var pressing = KeepPressingAsync(target, ["AddButton", "FinishButton"], operatorCts.Token);
            report = await runner.RunAsync(configuration);
            operatorCts.Cancel();
            await pressing;
        }

        Assert.All(report.FieldResults, field =>
        {
            Assert.Equal(FieldCheckOutcome.Found, field.Outcome);
            Assert.Equal(CaptureResult.UiaProviderId, field.ProviderId);
            Assert.True(field.Confidence > 0);
        });
        Assert.All(report.TriggerResults, trigger => Assert.Equal(TriggerCheckOutcome.Detected, trigger.Outcome));
        var approval = report.ToApproval(DateTimeOffset.UtcNow);
        Assert.NotNull(approval);

        // 3. Activation gate: no approval, or an approval for different content, is refused.
        var outboxPath = Path.Combine(_workDirectory, "outbox.db");
        var logWriter = new StringWriter();
        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        var runtime = CreateRuntime(factories, new SqliteEventOutbox(outboxPath, new DpapiPayloadProtector()), logWriter, diagnostics);

        Assert.Equal(ActivationStatus.NotTested, (await runtime.ActivateAsync(configuration, null, CancellationToken.None)).Status);
        var edited = configuration with { Name = configuration.Name + " (edited)" };
        Assert.Equal(ActivationStatus.NotTested, (await runtime.ActivateAsync(edited, approval, CancellationToken.None)).Status);

        // 4. Activate the tested configuration and play the user: two medicines, then finish.
        using var activationCts = new CancellationTokenSource();
        var activation = runtime.ActivateAsync(configuration, approval, activationCts.Token);
        try
        {
            // Both triggers' UIA subscriptions must be live before the first click, or the
            // click would be lost - the runtime reports that as a visible monitoring state.
            await WaitUntilAsync(
                () => diagnostics.Count(d => d.Code == RuntimeDiagnosticCode.TriggerWatchStarted) >= 2,
                TimeSpan.FromSeconds(20),
                () => Describe(diagnostics));

            SetMedicine(target, FirstMedicine);
            Press(target, "AddButton");
            await WaitForPersistedAsync(diagnostics, "item_added", 1);

            // Two genuine clicks closer together than the trigger de-duplication window
            // (500ms, a double-click) are deliberately collapsed into one; a real operator
            // typing a second medicine is far slower than that.
            await Task.Delay(TimeSpan.FromMilliseconds(800));
            SetMedicine(target, SecondMedicine);
            Press(target, "AddButton");
            await WaitForPersistedAsync(diagnostics, "item_added", 2);

            await Task.Delay(TimeSpan.FromMilliseconds(800));
            Press(target, "FinishButton");
            await WaitForPersistedAsync(diagnostics, "budget_finished", 1);
        }
        finally
        {
            activationCts.Cancel();
        }

        Assert.Equal(ActivationStatus.Activated, (await activation).Status);
        Assert.DoesNotContain(diagnostics, d => d.Severity == RuntimeDiagnosticSeverity.Error);

        // 5. "Restart": a brand-new outbox instance over the same file still holds all three
        //    events, in order, for one session, with the captured values intact.
        SqliteConnection.ClearAllPools();
        var reopened = new SqliteEventOutbox(outboxPath, new DpapiPayloadProtector());
        var pending = await reopened.ReadPendingAsync(CancellationToken.None);

        Assert.Equal(["item_added", "item_added", "budget_finished"], pending.Select(e => e.Type));
        Assert.Equal([1L, 2L, 3L], pending.Select(e => e.Sequence));
        Assert.Single(pending.Select(e => e.SessionId).Distinct());
        Assert.All(pending, e => Assert.Equal(configuration.Id, e.ConfigurationId));

        AssertItem(FirstMedicine, pending[0].Payload.Items.Single());
        Assert.Equal(2, pending[1].Payload.Items.Length);
        AssertItem(SecondMedicine, pending[1].Payload.Items[1]);
        var finished = pending[2].Payload.Items;
        Assert.Equal(2, finished.Length);
        AssertItem(FirstMedicine, finished[0]);
        AssertItem(SecondMedicine, finished[1]);

        // 6. Privacy: captured values never appear in the database bytes (payloads are DPAPI
        //    ciphertext) nor in the technical log, which still records IDs and codes.
        SqliteConnection.ClearAllPools();
        var databaseBytes = await File.ReadAllBytesAsync(outboxPath);
        var logText = logWriter.ToString();
        foreach (var value in FirstMedicine.Concat(SecondMedicine))
        {
            Assert.False(Contains(databaseBytes, Encoding.UTF8.GetBytes(value)), $"'{value}' found in plaintext (UTF-8) in the outbox file.");
            Assert.False(Contains(databaseBytes, Encoding.Unicode.GetBytes(value)), $"'{value}' found in plaintext (UTF-16) in the outbox file.");
            Assert.DoesNotContain(value, logText, StringComparison.Ordinal);
        }

        Assert.Contains("EventPersisted", logText, StringComparison.Ordinal);
        Assert.Contains("add_item", logText, StringComparison.Ordinal);
    }

    private static AgentRuntime CreateRuntime(
        UiAutomationRuntimeFactories factories,
        SqliteEventOutbox outbox,
        TextWriter logWriter,
        ConcurrentQueue<RuntimeDiagnostic> diagnostics)
    {
        var runtime = new AgentRuntime(
            new WindowsApplicationInstanceSource(pollInterval: TimeSpan.FromMilliseconds(100)),
            factories.CreateTriggerProvider,
            factories.CreateSelectorResolver,
            factories.CreateCaptureProvider,
            outbox,
            new StructuredTechnicalLog(logWriter));
        runtime.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);
        return runtime;
    }

    private static IntegrationConfiguration BuildConfiguration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "milestone-e2e",
        "Milestone end-to-end",
        new ApplicationDefinition(ProcessName, WindowTitle),
        [
            new FieldDefinition("medication", "budget", "Medicamento", Required: true, Selector: Fingerprint("MedicationTextBox", "ControlType.Edit")),
            new FieldDefinition("concentration", "budget", "Concentração", Required: true, Selector: Fingerprint("ConcentrationTextBox", "ControlType.Edit")),
            new FieldDefinition("quantity", "budget", "Quantidade", Required: true, Selector: Fingerprint("QuantityTextBox", "ControlType.Edit")),
        ],
        [
            new StageDefinition("budget", "Orçamento"),
        ],
        [
            new TriggerDefinition(
                "add_item",
                "budget",
                Fingerprint("AddButton", "ControlType.Button"),
                "Invoke",
                [new CaptureFieldsAction(["medication", "concentration", "quantity"]), new EmitEventAction("item_added")]),
            new TriggerDefinition(
                "finish_budget",
                "budget",
                Fingerprint("FinishButton", "ControlType.Button"),
                "Invoke",
                [new FinishSessionAction()]),
        ]);

    private static ElementFingerprint Fingerprint(string automationId, string controlType) =>
        new(ProcessName, WindowTitle, AutomationId: automationId, ControlType: controlType);

    private static void AssertItem(string[] expected, ImmutableDictionary<string, string> item)
    {
        Assert.Equal(expected[0], item["medication"]);
        Assert.Equal(expected[1], item["concentration"]);
        Assert.Equal(expected[2], item["quantity"]);
    }

    private static void SetMedicine(TestTargetLauncher target, string[] medicine)
    {
        SetText(target, "MedicationTextBox", medicine[0]);
        SetText(target, "ConcentrationTextBox", medicine[1]);
        SetText(target, "QuantityTextBox", medicine[2]);
    }

    private static void SetText(TestTargetLauncher target, string automationId, string value)
    {
        var pattern = (ValuePattern)Find(target, automationId).GetCurrentPattern(ValuePattern.Pattern);
        pattern.SetValue(value);
    }

    private static void Press(TestTargetLauncher target, string automationId)
    {
        var pattern = (InvokePattern)Find(target, automationId).GetCurrentPattern(InvokePattern.Pattern);
        pattern.Invoke();
    }

    private static AutomationElement Find(TestTargetLauncher target, string automationId) =>
        target.Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? throw new InvalidOperationException($"TestTarget element '{automationId}' not found.");

    private static async Task KeepPressingAsync(TestTargetLauncher target, string[] automationIds, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                foreach (var automationId in automationIds)
                {
                    Press(target, automationId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static Task WaitForPersistedAsync(ConcurrentQueue<RuntimeDiagnostic> diagnostics, string eventType, int count) =>
        WaitUntilAsync(
            () => diagnostics.Count(d => d.Code == RuntimeDiagnosticCode.EventPersisted && d.EventType == eventType) >= count,
            TimeSpan.FromSeconds(15),
            () => Describe(diagnostics));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.True(condition(), "Timed out. Diagnostics so far: " + describe());
    }

    private static string Describe(IEnumerable<RuntimeDiagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => $"{d.Code}/{d.Severity}/{d.TriggerId}/{d.FieldId}/{d.EventType}/{d.FailureCode}"));

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
