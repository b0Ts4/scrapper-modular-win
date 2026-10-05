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
    public async Task Persisted_events_carry_the_content_revision_of_the_running_configuration()
    {
        var outbox = new FakeEventOutbox();
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

        await CreateCoordinator(triggerProvider, resolver, captureProvider, outbox, new FakeTechnicalLog()).RunAsync(CancellationToken.None);

        var expected = Prescriva.Agent.Application.Testing.ConfigurationFingerprint.Compute(BuildConfiguration(requireName: true, requireNote: true));
        Assert.Equal(expected, Assert.Single(outbox.Appended).ConfigurationRevision);
    }

    [Fact]
    public async Task A_field_not_found_on_the_first_attempt_is_retried_and_captured()
    {
        var resolver = new SequencedSelectorResolver(new()
        {
            [NameFieldId] = [SelectorResolution.NotFound("not yet"), SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95)],
            [NoteFieldId] = [SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95)],
        });
        var (outbox, log, _) = await RunAddAsync(resolver, new CountingCaptureProvider(new()
        {
            [NameFieldId] = [Captured("Dipirona", NameFieldId)],
            [NoteFieldId] = [Captured("nota", NoteFieldId)],
        }));

        Assert.Equal("Dipirona", Assert.Single(outbox.Appended).Payload.Fields[NameFieldId]);
        Assert.Equal(2, resolver.Calls(NameFieldId));
        var retried = Assert.Single(log.Entries, entry => entry.Code == "field_capture_retried");
        Assert.Equal(NameFieldId, retried.FieldId);
        Assert.DoesNotContain("Dipirona", retried.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_element_temporarily_unavailable_for_reading_is_retried_and_captured()
    {
        var capture = new CountingCaptureProvider(new()
        {
            [NameFieldId] = [new CaptureResult(CaptureOutcome.ElementUnavailable, null, "uia", 0, TimeSpan.Zero, []), Captured("Dipirona", NameFieldId)],
            [NoteFieldId] = [Captured("nota", NoteFieldId)],
        });
        var (outbox, _, _) = await RunAddAsync(FoundResolver(), capture);

        Assert.Equal("Dipirona", Assert.Single(outbox.Appended).Payload.Fields[NameFieldId]);
        Assert.Equal(2, capture.Calls(NameFieldId));
    }

    [Fact]
    public async Task An_ambiguous_selector_is_never_retried()
    {
        var resolver = new SequencedSelectorResolver(new()
        {
            [NameFieldId] = [SelectorResolution.Ambiguous(0.5), SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95)],
            [NoteFieldId] = [SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95)],
        });
        var (outbox, _, diagnostics) = await RunAddAsync(resolver, FoundCapture());

        Assert.Empty(outbox.Appended);
        Assert.Equal(1, resolver.Calls(NameFieldId));
        var rejection = Assert.Single(diagnostics, d => d.Code == RuntimeDiagnosticCode.SessionRejected);
        Assert.Equal(SessionFailureCode.CaptureFailed, rejection.FailureCode);
        Assert.Equal(NameFieldId, rejection.FieldId);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_never_retried()
    {
        var capture = new CountingCaptureProvider(new()
        {
            [NameFieldId] = [new CaptureResult(CaptureOutcome.TooLarge, null, "uia", 0, TimeSpan.Zero, []), Captured("Dipirona", NameFieldId)],
            [NoteFieldId] = [Captured("nota", NoteFieldId)],
        });
        var (outbox, _, _) = await RunAddAsync(FoundResolver(), capture);

        Assert.Empty(outbox.Appended);
        Assert.Equal(1, capture.Calls(NameFieldId));
    }

    [Fact]
    public async Task A_field_that_stays_missing_exhausts_the_bounded_attempts_and_is_rejected_visibly()
    {
        var resolver = new SequencedSelectorResolver(new()
        {
            [NameFieldId] = [SelectorResolution.NotFound("gone")],
            [NoteFieldId] = [SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95)],
        });
        var (outbox, _, diagnostics) = await RunAddAsync(resolver, FoundCapture());

        Assert.Empty(outbox.Appended);
        Assert.Equal(CaptureRetryPolicy.Default.MaxAttempts, resolver.Calls(NameFieldId));
        var rejection = Assert.Single(diagnostics, d => d.Code == RuntimeDiagnosticCode.SessionRejected);
        Assert.Equal(SessionFailureCode.CaptureFailed, rejection.FailureCode);
        Assert.Equal(NameFieldId, rejection.FieldId);
    }

    [Fact]
    public async Task Cancellation_interrupts_the_wait_between_attempts()
    {
        var resolver = new SequencedSelectorResolver(new()
        {
            [NameFieldId] = [SelectorResolution.NotFound("gone")],
            [NoteFieldId] = [SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95)],
        });
        var outbox = new FakeEventOutbox();
        var coordinator = CreateCoordinator(
            new FakeTriggerProvider(new() { [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)] }),
            resolver, FoundCapture(), outbox, new FakeTechnicalLog(),
            captureRetry: new CaptureRetryPolicy(3, [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)]));
        using var cancellation = new CancellationTokenSource();

        var run = coordinator.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => resolver.Calls(NameFieldId) == 1);
        var cancelledAt = DateTime.UtcNow;
        cancellation.Cancel();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(DateTime.UtcNow - cancelledAt < TimeSpan.FromSeconds(5), "The retry wait ignored cancellation.");
        Assert.Equal(1, resolver.Calls(NameFieldId));
        Assert.Empty(outbox.Appended);
    }

    private static SequencedSelectorResolver FoundResolver() => new(new()
    {
        [NameFieldId] = [SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95)],
        [NoteFieldId] = [SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95)],
    });

    private static CountingCaptureProvider FoundCapture() => new(new()
    {
        [NameFieldId] = [Captured("Dipirona", NameFieldId)],
        [NoteFieldId] = [Captured("nota", NoteFieldId)],
    });

    private static async Task<(FakeEventOutbox Outbox, FakeTechnicalLog Log, ConcurrentQueue<RuntimeDiagnostic> Diagnostics)> RunAddAsync(
        ISelectorResolver resolver,
        ICaptureProvider capture)
    {
        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        var coordinator = CreateCoordinator(
            new FakeTriggerProvider(new() { [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)] }),
            resolver, capture, outbox, log);
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);
        await coordinator.RunAsync(CancellationToken.None);
        return (outbox, log, diagnostics);
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

    [Fact]
    public async Task A_trigger_watch_becoming_live_publishes_a_visible_monitoring_diagnostic()
    {
        var log = new FakeTechnicalLog();
        var triggerProvider = new FakeTriggerProvider(new());
        var coordinator = CreateCoordinator(
            triggerProvider, new FakeSelectorResolver(new()), new FakeCaptureProvider(new()), new FakeEventOutbox(), log);
        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);

        await coordinator.RunAsync(CancellationToken.None);

        var started = diagnostics.Where(d => d.Code == RuntimeDiagnosticCode.TriggerWatchStarted).ToArray();
        Assert.Equal([AddTriggerId, FinishTriggerId], started.Select(d => d.TriggerId).Order());
        Assert.All(started, d =>
        {
            Assert.Equal(RuntimeDiagnosticSeverity.Info, d.Severity);
            Assert.Equal(SessionId, d.SessionId);
        });
        Assert.Contains(log.Entries, entry => entry.Code == nameof(RuntimeDiagnosticCode.TriggerWatchStarted) && entry.TriggerId == AddTriggerId);
    }

    [Fact]
    public async Task A_trigger_watch_that_fails_is_reported_once_retried_and_does_not_stop_the_other_triggers()
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
        var triggerProvider = new FakeTriggerProvider(
            new() { [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)] },
            failingTriggerId: FinishTriggerId);
        var coordinator = CreateCoordinator(triggerProvider, resolver, captureProvider, outbox, log);
        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);

        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(cts.Token);
        await WaitUntilAsync(() => triggerProvider.FailedAttempts >= 3);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Retried while failing, but the operator sees one error per failure streak.
        var failed = Assert.Single(diagnostics, d => d.Code == RuntimeDiagnosticCode.TriggerWatchFailed);
        Assert.Equal(RuntimeDiagnosticSeverity.Error, failed.Severity);
        Assert.Equal(FinishTriggerId, failed.TriggerId);
        Assert.Single(outbox.Appended); // the Add trigger still worked
        Assert.Single(log.Entries, entry =>
            entry.Code == nameof(RuntimeDiagnosticCode.TriggerWatchFailed) &&
            entry.Level == TechnicalLogLevel.Error &&
            entry.TriggerId == FinishTriggerId);
    }

    [Fact]
    public async Task A_trigger_watch_that_recovers_reports_monitoring_again_and_handles_occurrences()
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
        var triggerProvider = new FakeTriggerProvider(
            new() { [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)] },
            failingTriggerId: AddTriggerId,
            failuresBeforeSuccess: 2);
        var coordinator = CreateCoordinator(triggerProvider, resolver, captureProvider, outbox, log);
        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);

        await coordinator.RunAsync(CancellationToken.None);

        var addDiagnostics = diagnostics.Where(d => d.TriggerId == AddTriggerId).Select(d => d.Code).ToArray();
        Assert.Equal(
            [RuntimeDiagnosticCode.TriggerWatchFailed, RuntimeDiagnosticCode.TriggerWatchStarted, RuntimeDiagnosticCode.EventPersisted],
            addDiagnostics);
        Assert.Single(outbox.Appended);
    }

    [Fact]
    public async Task A_watch_failure_followed_by_the_session_ending_within_the_retry_delay_is_not_reported()
    {
        // The application closing makes its elements disappear a moment before the
        // instance source reports the process gone; that is not a failure to show.
        var log = new FakeTechnicalLog();
        var triggerProvider = new FakeTriggerProvider(new(), failingTriggerId: FinishTriggerId);
        var coordinator = CreateCoordinator(
            triggerProvider, new FakeSelectorResolver(new()), new FakeCaptureProvider(new()), new FakeEventOutbox(), log,
            triggerRetryDelay: TimeSpan.FromSeconds(5));
        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);

        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(cts.Token);
        await WaitUntilAsync(() => triggerProvider.FailedAttempts >= 1);
        await coordinator.CloseAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.DoesNotContain(diagnostics, d => d.Code == RuntimeDiagnosticCode.TriggerWatchFailed);
        Assert.Contains(diagnostics, d => d.Code == RuntimeDiagnosticCode.SessionClosed);
        Assert.DoesNotContain(log.Entries, entry => entry.Level == TechnicalLogLevel.Error);
    }

    [Fact]
    public async Task A_file_field_stores_its_attachment_before_the_event_that_references_it()
    {
        var outbox = new FakeEventOutbox();
        var attachments = new InMemoryAttachmentStore(outbox);
        var content = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var coordinator = CreateFileCoordinator(
            new CaptureResult(CaptureOutcome.Captured, null, CaptureResult.UiaProviderId, 1.0, TimeSpan.Zero, [],
                new CapturedAttachment(content, "receita.pdf", "application/pdf", AttachmentSource.File)),
            outbox, attachments);

        await coordinator.RunAsync(CancellationToken.None);

        var persisted = Assert.Single(outbox.Appended);
        var reference = persisted.Payload.Fields["prescription"];
        Assert.True(AttachmentReference.IsReference(reference));
        Assert.Equal(content, await attachments.ReadAsync(reference, CancellationToken.None));
        Assert.True(attachments.SavedBeforeAnyEvent, "The attachment must be stored before the event referencing it is appended.");
    }

    [Theory]
    [InlineData(CaptureOutcome.TooLarge)]
    [InlineData(CaptureOutcome.Obscured)]
    [InlineData(CaptureOutcome.FileUnavailable)]
    public async Task A_file_that_cannot_be_captured_rejects_the_occurrence_and_stores_nothing(CaptureOutcome outcome)
    {
        var outbox = new FakeEventOutbox();
        var attachments = new InMemoryAttachmentStore(outbox);
        var coordinator = CreateFileCoordinator(new CaptureResult(outcome, null, CaptureResult.UiaProviderId, 0, TimeSpan.Zero, []), outbox, attachments);
        var diagnostics = new ConcurrentQueue<RuntimeDiagnostic>();
        coordinator.DiagnosticPublished += (_, diagnostic) => diagnostics.Enqueue(diagnostic);

        await coordinator.RunAsync(CancellationToken.None);

        Assert.Empty(outbox.Appended);
        Assert.Equal(0, attachments.Count);
        var rejected = Assert.Single(diagnostics, d => d.Code == RuntimeDiagnosticCode.SessionRejected);
        Assert.Equal(SessionFailureCode.CaptureFailed, rejected.FailureCode);
        Assert.Equal("prescription", rejected.FieldId);
    }

    private static SessionCoordinator CreateFileCoordinator(CaptureResult capture, FakeEventOutbox outbox, InMemoryAttachmentStore attachments)
    {
        var configuration = new IntegrationConfiguration(
            IntegrationConfiguration.CurrentSchemaVersion,
            "file-config",
            "File configuration",
            new ApplicationDefinition("Prescriva.Agent.TestTarget", "Prescriva Agent Test Target"),
            [new FieldDefinition("prescription", StageId, "Receita", Required: true, Selector: Fingerprint("prescription"), Kind: FieldKind.File)],
            [new StageDefinition(StageId, "Entry")],
            [new TriggerDefinition(AddTriggerId, StageId, Fingerprint("AddButton"), "Invoke",
                [new CaptureFieldsAction(["prescription"]), new EmitEventAction("item_added")])]);

        return new SessionCoordinator(
            SessionId,
            StageId,
            configuration,
            new FakeTriggerProvider(new() { [AddTriggerId] = [new TriggerSignal(SessionId, AddTriggerId, Now)] }),
            new FakeSelectorResolver(new() { ["prescription"] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95) }),
            new FakeCaptureProvider(new() { ["prescription"] = capture }),
            outbox,
            new FakeTechnicalLog(),
            clock: () => Now,
            attachments: attachments);
    }

    private sealed class InMemoryAttachmentStore(FakeEventOutbox outbox) : IAttachmentStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _content = new();

        public bool SavedBeforeAnyEvent { get; private set; }

        public int Count => _content.Count;

        public Task<AttachmentInfo> SaveAsync(CapturedAttachment attachment, CancellationToken cancellationToken)
        {
            SavedBeforeAnyEvent = outbox.Appended.Count == 0;
            var reference = AttachmentReference.For(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(attachment.Content)));
            _content[reference] = attachment.Content;
            return Task.FromResult(new AttachmentInfo(reference, attachment.FileName, attachment.ContentType, attachment.Content.Length, attachment.Source));
        }

        public Task<AttachmentInfo?> GetInfoAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<AttachmentInfo?>(null);

        public Task<byte[]?> ReadAsync(string reference, CancellationToken cancellationToken) =>
            Task.FromResult(_content.TryGetValue(reference, out var content) ? content : null);

        public Task<int> DeleteUnreferencedAsync(IReadOnlyCollection<string> referenced, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for the expected condition.");
    }

    private static SessionCoordinator CreateCoordinator(
        FakeTriggerProvider triggerProvider,
        ISelectorResolver resolver,
        ICaptureProvider captureProvider,
        FakeEventOutbox outbox,
        FakeTechnicalLog log,
        bool requireName = true,
        bool requireNote = true,
        TimeSpan? triggerRetryDelay = null,
        CaptureRetryPolicy? captureRetry = null)
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
            clock: () => Now,
            triggerRetryDelay: triggerRetryDelay ?? TimeSpan.FromMilliseconds(10),
            captureRetry: captureRetry ?? new CaptureRetryPolicy(CaptureRetryPolicy.Default.MaxAttempts, [TimeSpan.Zero, TimeSpan.Zero]));
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

    private sealed class FakeTriggerProvider(
        Dictionary<string, TriggerSignal[]> signalsByTrigger,
        string? failingTriggerId = null,
        int failuresBeforeSuccess = int.MaxValue) : ITriggerProvider
    {
        private int _failedAttempts;

        public event EventHandler<string>? WatchEstablished;

        public int FailedAttempts => Volatile.Read(ref _failedAttempts);

        public async IAsyncEnumerable<TriggerSignal> WatchAsync(
            TriggerDefinition trigger,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (trigger.Id == failingTriggerId && FailedAttempts < failuresBeforeSuccess)
            {
                Interlocked.Increment(ref _failedAttempts);
                throw new InvalidOperationException("The trigger's element could not be resolved.");
            }

            WatchEstablished?.Invoke(this, trigger.Id);
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

    /// <summary>Returns each field's resolutions in order, repeating the last one.</summary>
    private sealed class SequencedSelectorResolver(Dictionary<string, SelectorResolution[]> resolutionsByAutomationId) : ISelectorResolver
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public int Calls(string automationId) => _calls.GetValueOrDefault(automationId);

        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken)
        {
            var key = fingerprint.AutomationId ?? string.Empty;
            var call = _calls.AddOrUpdate(key, 1, (_, count) => count + 1);
            return Task.FromResult(resolutionsByAutomationId.TryGetValue(key, out var sequence)
                ? sequence[Math.Min(call, sequence.Length) - 1]
                : SelectorResolution.NotFound("no fake resolution configured"));
        }
    }

    /// <summary>Returns each field's capture results in order, repeating the last one.</summary>
    private sealed class CountingCaptureProvider(Dictionary<string, CaptureResult[]> resultsByFieldId) : ICaptureProvider
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public int Calls(string fieldId) => _calls.GetValueOrDefault(fieldId);

        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken)
        {
            var call = _calls.AddOrUpdate(field.Id, 1, (_, count) => count + 1);
            var sequence = resultsByFieldId[field.Id];
            return Task.FromResult(sequence[Math.Min(call, sequence.Length) - 1]);
        }
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
