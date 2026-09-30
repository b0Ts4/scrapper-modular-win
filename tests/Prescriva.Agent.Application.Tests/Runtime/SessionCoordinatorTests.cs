using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Diagnostics;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Domain.Sessions;

namespace Prescriva.Agent.Application.Tests.Runtime;

/// <summary>
/// Exercises SessionCoordinator against fakes for every one of the four interfaces it
/// consumes (ITriggerProvider, ISelectorResolver, ICaptureProvider, IEventOutbox) plus a
/// fake ITechnicalLog - no real UI Automation or SQLite involved, same style as
/// InspectionControllerTests. Covers every Step-1 scenario from the task brief: capturing
/// required fields and persisting item_added, a missing required field preventing append,
/// a selector fallback (ambiguous match) on an optional field, Finish emitting
/// budget_finished, application close ending the session, and technical logs excluding
/// captured values while retaining IDs/codes/timings.
/// </summary>
public sealed class SessionCoordinatorTests
{
    private const string StageId = "entry";
    private const string NameFieldId = "name";
    private const string NoteFieldId = "note";
    private const string AddTriggerId = "add";
    private const string FinishTriggerId = "finish";

    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Add_captures_required_fields_and_persists_item_added()
    {
        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var resolver = new FakeSelectorResolver(new()
        {
            [NameFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
            [NoteFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
        });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [NameFieldId] = Captured("Dipirona", NameFieldId),
            [NoteFieldId] = Captured("Take with food", NoteFieldId),
        });
        var triggerProvider = new FakeTriggerProvider(new()
        {
            [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)],
        });

        var coordinator = CreateCoordinator(triggerProvider, resolver, captureProvider, outbox, log);

        await coordinator.RunAsync(CancellationToken.None);

        var appended = Assert.Single(outbox.Appended);
        Assert.Equal("item_added", appended.Type);
        Assert.Equal("Dipirona", appended.Payload.Fields[NameFieldId]);
        Assert.Equal("Take with food", appended.Payload.Fields[NoteFieldId]);
        Assert.Equal(SessionState.Active, coordinator.CurrentSession.State);
        Assert.Equal(1, coordinator.CurrentSession.LastSequence);
    }

    [Fact]
    public async Task Add_with_missing_required_field_is_rejected_and_never_appended()
    {
        // The required "name" field resolves and reads cleanly (no typed CaptureFailure) but
        // the live control reported a blank value - the one scenario SessionEngine treats as
        // "missing required field" rather than "capture failed" (see docs/sessions.md).
        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var resolver = new FakeSelectorResolver(new()
        {
            [NameFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
            [NoteFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
        });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [NameFieldId] = Captured(string.Empty, NameFieldId),
            [NoteFieldId] = Captured("a note", NoteFieldId),
        });
        var triggerProvider = new FakeTriggerProvider(new()
        {
            [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)],
        });

        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        var coordinator = CreateCoordinator(triggerProvider, resolver, captureProvider, outbox, log);
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);

        await coordinator.RunAsync(CancellationToken.None);

        Assert.Empty(outbox.Appended);
        Assert.Equal(SessionState.Active, coordinator.CurrentSession.State);
        Assert.Equal(0, coordinator.CurrentSession.LastSequence);

        var rejection = Assert.Single(diagnostics, diagnostic => diagnostic.Code == RuntimeDiagnosticCode.SessionRejected);
        Assert.Equal(RuntimeDiagnosticSeverity.Error, rejection.Severity);
        Assert.Equal(SessionFailureCode.MissingRequiredField, rejection.FailureCode);
        Assert.Equal(NameFieldId, rejection.FieldId);
    }

    [Fact]
    public async Task Add_with_a_low_confidence_match_still_persists_and_raises_a_fallback_diagnostic()
    {
        // The resolver still reports "Found" for the optional "note" field - it already
        // decided the match was safe to act on - but at a confidence below the coordinator's
        // own fallback threshold. The transition must still succeed (SessionEngine never sees
        // a typed CaptureFailure here), while an operator-facing fallback diagnostic flags the
        // low-confidence match for review.
        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var resolver = new FakeSelectorResolver(new()
        {
            [NameFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
            [NoteFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.65),
        });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [NameFieldId] = Captured("Dipirona", NameFieldId),
            [NoteFieldId] = Captured("Take with water", NoteFieldId),
        });
        var triggerProvider = new FakeTriggerProvider(new()
        {
            [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)],
        });

        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        var coordinator = CreateCoordinator(triggerProvider, resolver, captureProvider, outbox, log);
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);

        await coordinator.RunAsync(CancellationToken.None);

        var appended = Assert.Single(outbox.Appended);
        Assert.Equal("item_added", appended.Type);
        Assert.Equal("Take with water", appended.Payload.Fields[NoteFieldId]);

        var fallback = Assert.Single(diagnostics, diagnostic => diagnostic.Code == RuntimeDiagnosticCode.SelectorFallback);
        Assert.Equal(RuntimeDiagnosticSeverity.Warning, fallback.Severity);
        Assert.Equal(NoteFieldId, fallback.FieldId);
        Assert.Equal(0.65, fallback.Confidence);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == RuntimeDiagnosticCode.EventPersisted);
    }

    [Fact]
    public async Task Finish_emits_budget_finished_and_ends_the_session()
    {
        // FinishSessionAction's own trigger captures nothing first, so with no Add having run
        // yet the accumulated values are still empty - both fields must be optional here so
        // Emit's required-field check (across every stage, per docs/sessions.md) does not
        // itself reject the occurrence; that rejection path is already covered by its own test.
        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var resolver = new FakeSelectorResolver(new());
        var captureProvider = new FakeCaptureProvider(new());
        var triggerProvider = new FakeTriggerProvider(new()
        {
            [FinishTriggerId] = [new TriggerSignal(SessionId, FinishTriggerId, Now)],
        });

        var coordinator = CreateCoordinator(triggerProvider, resolver, captureProvider, outbox, log, requireName: false, requireNote: false);

        await coordinator.RunAsync(CancellationToken.None);

        var appended = Assert.Single(outbox.Appended);
        Assert.Equal("budget_finished", appended.Type);
        Assert.Equal(SessionState.Finished, coordinator.CurrentSession.State);
    }

    [Fact]
    public async Task Application_close_ends_an_active_session_without_emitting_an_event()
    {
        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var coordinator = CreateCoordinator(
            new FakeTriggerProvider(new()),
            new FakeSelectorResolver(new()),
            new FakeCaptureProvider(new()),
            outbox,
            log);

        var diagnostics = new List<RuntimeDiagnostic>();
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Add(diagnostic);

        await coordinator.CloseAsync();

        Assert.Empty(outbox.Appended);
        Assert.Equal(SessionState.Cancelled, coordinator.CurrentSession.State);
        Assert.Empty(coordinator.CurrentSession.Values);
        Assert.Single(diagnostics, diagnostic => diagnostic.Code == RuntimeDiagnosticCode.SessionClosed);
    }

    [Fact]
    public async Task Technical_logs_never_contain_captured_field_values()
    {
        const string secretValue = "Dipirona-500mg-super-secret";

        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var resolver = new FakeSelectorResolver(new()
        {
            [NameFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
            [NoteFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
        });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [NameFieldId] = Captured(secretValue, NameFieldId),
            [NoteFieldId] = Captured(string.Empty, NoteFieldId),
        });
        var triggerProvider = new FakeTriggerProvider(new()
        {
            [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)],
        });

        var coordinator = CreateCoordinator(triggerProvider, resolver, captureProvider, outbox, log, requireNote: false);

        await coordinator.RunAsync(CancellationToken.None);

        Assert.Single(outbox.Appended); // sanity: the value really was captured and used
        Assert.NotEmpty(log.Entries);
        foreach (var entry in log.Entries)
        {
            Assert.DoesNotContain(secretValue, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, entry.Code, StringComparison.Ordinal);
        }

        // IDs, codes and timings must still be present on the per-field capture log entry.
        var captureEntry = Assert.Single(log.Entries, entry => entry.FieldId == NameFieldId);
        Assert.Equal(SessionId, captureEntry.SessionId);
        Assert.Equal(AddTriggerId, captureEntry.TriggerId);
        Assert.Equal(CaptureResult.UiaProviderId, captureEntry.Provider);
        Assert.NotNull(captureEntry.Confidence);
        Assert.NotNull(captureEntry.Elapsed);
    }

    private static SessionCoordinator CreateCoordinator(
        FakeTriggerProvider triggerProvider,
        FakeSelectorResolver resolver,
        FakeCaptureProvider captureProvider,
        FakeEventOutbox outbox,
        FakeTechnicalLog log,
        bool requireName = true,
        bool requireNote = true)
    {
        var configuration = BuildConfiguration(requireName, requireNote);
        var eventIds = new Queue<Guid>(Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()));
        return new SessionCoordinator(
            SessionId,
            StageId,
            configuration,
            triggerProvider,
            resolver,
            captureProvider,
            outbox,
            log,
            eventIdFactory: () => eventIds.Dequeue(),
            clock: () => Now);
    }

    private static IntegrationConfiguration BuildConfiguration(bool requireName, bool requireNote) => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "config-1",
        "Test configuration",
        new ApplicationDefinition("Prescriva.Agent.TestTarget", "Prescriva Agent Test Target"),
        [
            new FieldDefinition(NameFieldId, StageId, "Medication name", Required: requireName, Selector: Fingerprint(NameFieldId)),
            new FieldDefinition(NoteFieldId, StageId, "Note", Required: requireNote, Selector: Fingerprint(NoteFieldId)),
        ],
        [
            new StageDefinition(StageId, "Entry"),
        ],
        [
            new TriggerDefinition(
                AddTriggerId,
                StageId,
                Fingerprint("AddButton"),
                "Invoke",
                [new CaptureFieldsAction([NameFieldId, NoteFieldId]), new EmitEventAction("item_added")]),
            new TriggerDefinition(
                FinishTriggerId,
                StageId,
                Fingerprint("FinishButton"),
                "Invoke",
                [new FinishSessionAction()]),
        ]);

    private static ElementFingerprint Fingerprint(string automationId) =>
        new("Prescriva.Agent.TestTarget", "Prescriva Agent Test Target", AutomationId: automationId);

    private static CaptureResult Captured(string value, string fieldId) =>
        new(CaptureOutcome.Captured, value, CaptureResult.UiaProviderId, 0.97, TimeSpan.FromMilliseconds(5), [new PatternAttempt("Value", true)]);

    private sealed class FakeTriggerProvider(Dictionary<string, TriggerSignal[]> signalsByTrigger) : ITriggerProvider
    {
        public async IAsyncEnumerable<TriggerSignal> WatchAsync(
            TriggerDefinition trigger,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (signalsByTrigger.TryGetValue(trigger.Id, out var signals))
            {
                foreach (var signal in signals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    yield return signal;
                }
            }
        }
    }

    private sealed class FakeSelectorResolver(Dictionary<string, SelectorResolution> resolutionsByAutomationId) : ISelectorResolver
    {
        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken)
        {
            var key = fingerprint.AutomationId ?? string.Empty;
            return Task.FromResult(resolutionsByAutomationId.TryGetValue(key, out var resolution)
                ? resolution
                : SelectorResolution.NotFound("no fake resolution configured"));
        }
    }

    private sealed class FakeCaptureProvider(Dictionary<string, CaptureResult> resultsByFieldId) : ICaptureProvider
    {
        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken) =>
            Task.FromResult(resultsByFieldId.TryGetValue(field.Id, out var result)
                ? result
                : new CaptureResult(CaptureOutcome.ElementUnavailable, null, "fake", 0, TimeSpan.Zero, []));
    }

    private sealed class FakeResolvedElementHandle : ResolvedElementHandle
    {
        public static readonly FakeResolvedElementHandle Instance = new();
    }

    private sealed class FakeEventOutbox : IEventOutbox
    {
        private readonly ConcurrentQueue<DomainEvent> _appended = new();

        public IReadOnlyList<DomainEvent> Appended => _appended.ToArray();

        public Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken)
        {
            _appended.Enqueue(domainEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DomainEvent>> ReadPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainEvent>>(Appended);

        public Task MarkConfirmedAsync(Guid eventId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task QuarantineAsync(Guid eventId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteConfirmedBeforeAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeTechnicalLog : ITechnicalLog
    {
        private readonly ConcurrentQueue<TechnicalLogEntry> _entries = new();

        public IReadOnlyList<TechnicalLogEntry> Entries => _entries.ToArray();

        public void Log(TechnicalLogEntry entry) => _entries.Enqueue(entry);
    }
}
