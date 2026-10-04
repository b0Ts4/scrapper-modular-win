using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Desktop.Configuration;

/// <summary>
/// The Application-facing orchestration layer for the configurator vertical slice: build
/// up a draft <see cref="IntegrationConfiguration"/> (create integration, select
/// application, add stage/field/trigger with a validated semantic ID), save it (validating
/// via <see cref="ConfigurationValidator"/> before ever calling the store, so an invalid
/// configuration is never persisted), reload it, and drive resolve + read-value for a
/// saved field.
///
/// Deliberately handles only plain Application/Domain types - never
/// <c>System.Windows.Automation.AutomationElement</c> or any other Prescriva.Agent.Windows
/// type - even though it physically lives in Prescriva.Agent.Desktop. It is fully
/// testable with fakes for <see cref="IConfigurationStore"/>, <see cref="ISelectorResolver"/>
/// and <see cref="ICaptureProvider"/>, following the same pattern
/// <c>InspectionController</c> already established.
/// </summary>
public sealed class IntegrationEditorViewModel
{
    /// <summary>
    /// A semantic ID (a field or trigger's stable, human-assigned identifier) must start
    /// with a letter and contain only letters, digits and underscores - the same shape a
    /// programming-language identifier would take, since semantic IDs are referenced from
    /// trigger actions (e.g. <see cref="CaptureFieldsAction.FieldIds"/>) and eventually
    /// surface in session events.
    /// </summary>
    private static readonly Regex SemanticIdPattern = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly IConfigurationStore _store;
    private readonly ISelectorResolver _resolver;
    private readonly ICaptureProvider _captureProvider;
    private readonly Dictionary<string, ResolvedElementHandle> _resolvedHandles = new(StringComparer.Ordinal);

    public IntegrationEditorViewModel(
        IConfigurationStore store,
        ISelectorResolver resolver,
        ICaptureProvider captureProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(captureProvider);

        _store = store;
        _resolver = resolver;
        _captureProvider = captureProvider;
    }

    /// <summary>The draft configuration being edited, or null before <see cref="CreateIntegration"/> is called.</summary>
    public IntegrationConfiguration? Configuration { get; private set; }

    /// <summary>True while the in-memory <see cref="Configuration"/> has edits not yet persisted via <see cref="SaveAsync"/>.</summary>
    public bool HasUnsavedChanges { get; private set; }

    /// <summary>The validation errors from the most recent <see cref="SaveAsync"/> attempt, if any.</summary>
    public IReadOnlyList<ConfigurationValidationError> LastValidationErrors { get; private set; } =
        Array.Empty<ConfigurationValidationError>();

    /// <summary>Starts a new draft configuration. Any previous draft is discarded.</summary>
    public void CreateIntegration(string id, string name, ApplicationDefinition application)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(application);

        Configuration = new IntegrationConfiguration(
            IntegrationConfiguration.CurrentSchemaVersion,
            id,
            name,
            application,
            ImmutableArray<FieldDefinition>.Empty,
            ImmutableArray<StageDefinition>.Empty,
            ImmutableArray<TriggerDefinition>.Empty);

        _resolvedHandles.Clear();
        MarkDirty();
    }

    /// <summary>Changes which application the draft configuration targets.</summary>
    public void SelectApplication(ApplicationDefinition application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var current = RequireConfiguration();

        Configuration = current with { Application = application };
        MarkDirty();
    }

    /// <summary>Appends a stage to the draft configuration.</summary>
    public void AddStage(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var current = RequireConfiguration();
        if (current.Stages.Any(stage => stage.Id == id))
        {
            throw new InvalidOperationException($"A stage with ID '{id}' already exists.");
        }

        Configuration = current with { Stages = current.Stages.Add(new StageDefinition(id, name)) };
        MarkDirty();
    }

    /// <summary>
    /// Appends a field to the draft configuration, typically built from a confirmed
    /// <c>InspectionState.Fingerprint</c>. Rejects an invalid semantic ID before touching
    /// the configuration - it is never partially applied.
    /// </summary>
    public void AddField(string semanticId, string stageId, string meaning, bool required, ElementFingerprint selector)
    {
        ValidateSemanticId(semanticId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(meaning);
        ArgumentNullException.ThrowIfNull(selector);
        var current = RequireConfiguration();
        if (current.Fields.Any(field => field.Id == semanticId))
        {
            throw new InvalidOperationException($"A field with semantic ID '{semanticId}' already exists. Remove it first to replace its selector.");
        }

        var field = new FieldDefinition(semanticId, stageId, meaning, required, selector);
        Configuration = current with { Fields = current.Fields.Add(field) };
        MarkDirty();
    }

    /// <summary>
    /// Appends a trigger to the draft configuration. Rejects an invalid semantic ID before
    /// touching the configuration - it is never partially applied.
    /// </summary>
    public void AddTrigger(
        string semanticId,
        string stageId,
        ElementFingerprint selector,
        string observedEvent,
        ImmutableArray<TriggerActionDefinition> actions)
    {
        ValidateSemanticId(semanticId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedEvent);
        var current = RequireConfiguration();
        if (current.Triggers.Any(trigger => trigger.Id == semanticId))
        {
            throw new InvalidOperationException($"A trigger with semantic ID '{semanticId}' already exists. Replace its actions or remove it first.");
        }

        var trigger = new TriggerDefinition(semanticId, stageId, selector, observedEvent, actions);
        Configuration = current with { Triggers = current.Triggers.Add(trigger) };
        MarkDirty();
    }

    /// <summary>
    /// Removes a field. Refused - leaving the configuration untouched - while any trigger
    /// still captures it, naming those triggers, so an edit never leaves a dangling
    /// reference. Any handle resolved for the field is forgotten.
    /// </summary>
    public void RemoveField(string fieldId)
    {
        var current = RequireConfiguration();
        var field = FindField(fieldId);
        var users = current.Triggers
            .Where(trigger => trigger.Actions.OfType<CaptureFieldsAction>().Any(capture => capture.FieldIds.Contains(fieldId)))
            .Select(trigger => trigger.Id)
            .ToArray();
        if (users.Length > 0)
        {
            throw new InvalidOperationException(
                $"Field '{fieldId}' is captured by trigger(s) {string.Join(", ", users.Select(id => $"'{id}'"))}. Change or remove those triggers first.");
        }

        Configuration = current with { Fields = current.Fields.Remove(field) };
        _resolvedHandles.Remove(fieldId);
        MarkDirty();
    }

    /// <summary>Changes a field's meaning and whether it is required; its stage and selector are kept.</summary>
    public void UpdateField(string fieldId, string meaning, bool required)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meaning);
        var current = RequireConfiguration();
        var field = FindField(fieldId);

        Configuration = current with { Fields = current.Fields.Replace(field, field with { Meaning = meaning, Required = required }) };
        MarkDirty();
    }

    /// <summary>Removes a trigger.</summary>
    public void RemoveTrigger(string triggerId)
    {
        var current = RequireConfiguration();
        var trigger = FindTrigger(triggerId);

        Configuration = current with { Triggers = current.Triggers.Remove(trigger) };
        MarkDirty();
    }

    /// <summary>Replaces a trigger's ordered action list; its stage, selector and observed event are kept.</summary>
    public void ReplaceTriggerActions(string triggerId, ImmutableArray<TriggerActionDefinition> actions)
    {
        if (actions.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A trigger needs at least one action.", nameof(actions));
        }

        var current = RequireConfiguration();
        var trigger = FindTrigger(triggerId);

        Configuration = current with { Triggers = current.Triggers.Replace(trigger, trigger with { Actions = actions }) };
        MarkDirty();
    }

    /// <summary>
    /// Removes a stage. Refused while any field or trigger belongs to it, or any trigger
    /// transitions to it, naming them.
    /// </summary>
    public void RemoveStage(string stageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        var current = RequireConfiguration();
        var stage = current.Stages.FirstOrDefault(candidate => candidate.Id == stageId)
            ?? throw new InvalidOperationException($"No stage with ID '{stageId}' exists in the current configuration.");

        var users = current.Fields.Where(field => field.StageId == stageId).Select(field => field.Id)
            .Concat(current.Triggers
                .Where(trigger => trigger.StageId == stageId || trigger.Actions.OfType<TransitionStageAction>().Any(transition => transition.StageId == stageId))
                .Select(trigger => trigger.Id))
            .ToArray();
        if (users.Length > 0)
        {
            throw new InvalidOperationException(
                $"Stage '{stageId}' is still used by {string.Join(", ", users.Select(id => $"'{id}'"))}. Remove or change them first.");
        }

        Configuration = current with { Stages = current.Stages.Remove(stage) };
        MarkDirty();
    }

    /// <summary>
    /// Validates the draft configuration via <see cref="ConfigurationValidator"/> and only
    /// then calls <see cref="IConfigurationStore.SaveAsync"/> - an invalid configuration is
    /// never handed to the store, so it can never be persisted. Throws
    /// <see cref="ConfigurationValidationException"/> (without saving) when invalid.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var current = RequireConfiguration();

        var validation = ConfigurationValidator.Validate(current);
        LastValidationErrors = validation.Errors;
        if (!validation.IsValid)
        {
            throw new ConfigurationValidationException(validation.Errors);
        }

        await _store.SaveAsync(current, cancellationToken).ConfigureAwait(false);
        HasUnsavedChanges = false;
    }

    /// <summary>
    /// Discards the in-memory draft and reloads the configuration with the given ID from
    /// the store, preserving whatever selector fingerprints it contains exactly as the
    /// store returned them.
    /// </summary>
    public async Task ReloadAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Configuration = await _store.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        _resolvedHandles.Clear();
        LastValidationErrors = Array.Empty<ConfigurationValidationError>();
        HasUnsavedChanges = false;
    }

    /// <summary>
    /// Resolves a saved field's selector against the live desktop. On
    /// <see cref="SelectorResolutionStatus.Found"/>, caches the resulting handle so a
    /// subsequent <see cref="ReadFieldValueAsync"/> for the same field can read through it.
    /// </summary>
    public async Task<SelectorResolution> ResolveFieldAsync(string fieldId, CancellationToken cancellationToken = default)
    {
        var field = FindField(fieldId);
        var resolution = await _resolver.ResolveAsync(field.Selector, cancellationToken).ConfigureAwait(false);

        if (resolution.Status == SelectorResolutionStatus.Found && resolution.Handle is not null)
        {
            _resolvedHandles[fieldId] = resolution.Handle;
        }
        else
        {
            _resolvedHandles.Remove(fieldId);
        }

        return resolution;
    }

    /// <summary>
    /// Reads the current value of a field through the handle produced by a prior
    /// successful <see cref="ResolveFieldAsync"/> call. Throws
    /// <see cref="InvalidOperationException"/> if the field has not been resolved yet.
    /// </summary>
    public Task<CaptureResult> ReadFieldValueAsync(string fieldId, CancellationToken cancellationToken = default)
    {
        var field = FindField(fieldId);
        if (!_resolvedHandles.TryGetValue(fieldId, out var handle))
        {
            throw new InvalidOperationException(
                $"Field '{fieldId}' has not been resolved yet. Call {nameof(ResolveFieldAsync)} first.");
        }

        return _captureProvider.CaptureAsync(handle, field, cancellationToken);
    }

    private FieldDefinition FindField(string fieldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        var current = RequireConfiguration();

        foreach (var field in current.Fields)
        {
            if (string.Equals(field.Id, fieldId, StringComparison.Ordinal))
            {
                return field;
            }
        }

        throw new InvalidOperationException($"No field with semantic ID '{fieldId}' exists in the current configuration.");
    }

    private TriggerDefinition FindTrigger(string triggerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        return RequireConfiguration().Triggers.FirstOrDefault(trigger => trigger.Id == triggerId)
            ?? throw new InvalidOperationException($"No trigger with semantic ID '{triggerId}' exists in the current configuration.");
    }

    private IntegrationConfiguration RequireConfiguration()
    {
        return Configuration
            ?? throw new InvalidOperationException($"Call {nameof(CreateIntegration)} (or {nameof(ReloadAsync)}) before editing the configuration.");
    }

    private static void ValidateSemanticId(string? semanticId)
    {
        if (string.IsNullOrWhiteSpace(semanticId) || !SemanticIdPattern.IsMatch(semanticId))
        {
            throw new ArgumentException(
                $"'{semanticId}' is not a valid semantic ID. A semantic ID must start with a letter and contain only letters, digits and underscores.",
                nameof(semanticId));
        }
    }

    private void MarkDirty() => HasUnsavedChanges = true;
}
