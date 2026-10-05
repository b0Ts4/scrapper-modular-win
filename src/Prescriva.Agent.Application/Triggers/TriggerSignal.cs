namespace Prescriva.Agent.Application.Triggers;

/// <summary>
/// A single logical occurrence of a configured <see cref="Prescriva.Agent.Domain.Configuration.TriggerDefinition"/>
/// firing against a live UI - plain data only, produced by an <see cref="ITriggerProvider"/>
/// implementation. Deliberately carries no captured field values: those are read
/// separately (via the capture pipeline) once a signal has survived deduplication, so
/// this type - and anything that inspects it, notably <see cref="TriggerDeduplicator"/> -
/// never has a payload value available to key off.
/// </summary>
/// <param name="SessionId">The capture session this trigger is being observed on behalf of.</param>
/// <param name="TriggerId">The <see cref="Prescriva.Agent.Domain.Configuration.TriggerDefinition.Id"/> that fired.</param>
/// <param name="OccurredAt">When the provider observed the native UI event, used as the deduplication time window anchor.</param>
/// <param name="NativeOccurrenceId">
/// An opaque identity for the native event occurrence, when the underlying automation
/// framework exposes one. May be null - many native "invoked" events carry no
/// per-occurrence identity at all, in which case deduplication falls back to the time
/// window alone.
/// </param>
public sealed record TriggerSignal(
    Guid SessionId,
    string TriggerId,
    DateTimeOffset OccurredAt,
    string? NativeOccurrenceId = null);
