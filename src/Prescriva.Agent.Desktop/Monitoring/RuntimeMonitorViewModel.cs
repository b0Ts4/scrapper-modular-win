using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Domain.Sessions;
using Prescriva.Agent.Infrastructure.Events;

namespace Prescriva.Agent.Desktop.Monitoring;

/// <summary>One runtime diagnostic, already rendered as Portuguese text for the operator.</summary>
public sealed record DiagnosticDisplay(DateTimeOffset Timestamp, bool IsError, string Message);

/// <summary>
/// One persisted event as the operator sees it. <see cref="FieldsText"/> holds the
/// decrypted business values for on-screen display only: it is never logged and never
/// written anywhere (the outbox itself stores it as DPAPI ciphertext).
/// </summary>
public sealed record EventDisplay(
    long Sequence,
    string Type,
    Guid SessionId,
    DateTimeOffset Timestamp,
    int ItemCount,
    string FieldsText)
{
    /// <summary>One-line rendering for list display.</summary>
    public string Text => $"#{Sequence} {Type} — {ItemCount} item(ns) — {FieldsText}";
}

/// <summary>
/// The configurator's view of an active configuration: activates it through
/// <see cref="AgentRuntime"/> (which refuses an untested or since-edited configuration),
/// exposes whether it is monitoring, renders every <see cref="RuntimeDiagnostic"/> as
/// actionable Portuguese text, and lists the events persisted to the outbox, refreshed
/// each time one is persisted.
///
/// Plain INotifyPropertyChanged with no XAML dependency, testable against a real
/// <see cref="AgentRuntime"/> fed fakes. Like <c>TestModeViewModel</c>, updates are
/// marshalled to a host-supplied <see cref="Dispatcher"/>, or applied inline without one.
/// </summary>
public sealed class RuntimeMonitorViewModel : INotifyPropertyChanged
{
    private const int MaxDiagnostics = 200;

    private readonly AgentRuntime _runtime;
    private readonly IEventOutbox _outbox;
    private readonly Dispatcher? _dispatcher;
    private readonly object _gate = new();
    private readonly List<DiagnosticDisplay> _diagnostics = new();

    private IReadOnlyList<EventDisplay> _events = Array.Empty<EventDisplay>();
    private long _eventReadsStarted;
    private long _newestAppliedEventRead;

    private readonly OutboxCapacityPolicy _capacityPolicy;
    private readonly Func<CancellationToken, Task>? _clearLocalData;
    private readonly IAttachmentStore? _attachments;
    private OutboxCapacityAssessment? _capacity;
    private IntegrationHealthTracker? _health;

    private CancellationTokenSource? _activationCts;
    private Task<ActivationResult>? _activation;
    private bool _isMonitoring;
    private string _statusText = "Inativo.";

    /// <param name="capacityPolicy">Thresholds for the pending-events alert (defaults: 1000 warning, 5000 critical).</param>
    /// <param name="clearLocalData">
    /// The operator's explicit "clear local data" action (deletes the event queue and the
    /// technical log). Null when the host offers no such action.
    /// </param>
    public RuntimeMonitorViewModel(
        AgentRuntime runtime,
        IEventOutbox outbox,
        Dispatcher? dispatcher = null,
        OutboxCapacityPolicy? capacityPolicy = null,
        Func<CancellationToken, Task>? clearLocalData = null,
        IAttachmentStore? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(outbox);

        _runtime = runtime;
        _outbox = outbox;
        _dispatcher = dispatcher;
        _capacityPolicy = capacityPolicy ?? new OutboxCapacityPolicy();
        _clearLocalData = clearLocalData;
        _attachments = attachments;
        _runtime.DiagnosticPublished += OnDiagnosticPublished;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsMonitoring => _isMonitoring;

    public string StatusText => _statusText;

    /// <summary>
    /// A visible Portuguese alert once pending events reach the warning threshold, or null.
    /// Alerting never deletes anything: pending events stay until confirmed or until the
    /// operator explicitly clears local data.
    /// </summary>
    public string? CapacityAlert
    {
        get
        {
            var capacity = Volatile.Read(ref _capacity);
            return capacity?.Status switch
            {
                OutboxCapacityStatus.Warning =>
                    $"Atenção: {capacity.PendingCount} eventos pendentes na fila local (alerta a partir de {capacity.WarningThreshold}). Eles não serão apagados automaticamente.",
                OutboxCapacityStatus.Critical =>
                    $"Limite crítico: {capacity.PendingCount} eventos pendentes na fila local (crítico a partir de {capacity.CriticalThreshold}). Nada é apagado automaticamente; exporte ou limpe os dados locais.",
                _ => null,
            };
        }
    }

    /// <summary>The active integration's health, or null when nothing has been activated.</summary>
    public IntegrationHealthState? HealthState
    {
        get
        {
            lock (_gate)
            {
                return _health?.Current.State;
            }
        }
    }

    /// <summary>
    /// The integration's health with every reason (spec §8), in Portuguese: empty before
    /// any activation. Built only from IDs and confidence - never from a captured value.
    /// </summary>
    public string HealthText
    {
        get
        {
            IntegrationHealth? health;
            lock (_gate)
            {
                health = _health?.Current;
            }

            return health is null ? string.Empty : Describe(health);
        }
    }

    /// <summary>True when pending events reached the critical threshold.</summary>
    public bool IsCapacityCritical => Volatile.Read(ref _capacity)?.Status == OutboxCapacityStatus.Critical;

    /// <summary>
    /// Deletes local business data (event queue and technical log) through the host's
    /// action. Refused - returning false with an explanation - while a configuration is
    /// being monitored, or when the host offers no such action.
    /// </summary>
    public async Task<bool> ClearLocalDataAsync(CancellationToken cancellationToken = default)
    {
        if (_clearLocalData is null)
        {
            SetStatus(_isMonitoring, "A limpeza de dados locais não está disponível.");
            return false;
        }

        if (_activation is not null)
        {
            SetStatus(true, "Pare o monitoramento antes de apagar os dados locais.");
            return false;
        }

        await _clearLocalData(cancellationToken).ConfigureAwait(false);
        await RefreshEventsAsync(cancellationToken).ConfigureAwait(false);
        AddDiagnostic(new DiagnosticDisplay(DateTimeOffset.UtcNow, false, "Dados locais apagados pelo operador."));
        SetStatus(false, "Dados locais apagados: fila de eventos e log técnico.");
        return true;
    }

    /// <summary>
    /// The most recent diagnostics, oldest first (bounded). Always read under the lock
    /// from the single list every publisher appends to: diagnostics arrive concurrently
    /// from different sessions/triggers, and a snapshot taken by one publisher must never
    /// be able to overwrite a newer one taken by another.
    /// </summary>
    public IReadOnlyList<DiagnosticDisplay> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    /// <summary>Every pending event in the outbox, in append order (as of the newest completed read).</summary>
    public IReadOnlyList<EventDisplay> Events
    {
        get
        {
            lock (_gate)
            {
                return _events;
            }
        }
    }

    /// <summary>
    /// Activates <paramref name="configuration"/>. Returns <see cref="ActivationStatus.NotTested"/>
    /// (and stays inactive) when <paramref name="approval"/> is missing or does not match
    /// the configuration's current content; otherwise starts monitoring in the background
    /// and returns <see cref="ActivationStatus.Activated"/> until <see cref="StopAsync"/>.
    /// </summary>
    public async Task<ActivationStatus> StartAsync(IntegrationConfiguration configuration, ConfigurationApproval? approval)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_activation is not null)
        {
            throw new InvalidOperationException("Uma configuração já está ativa. Pare o monitoramento antes de ativar outra.");
        }

        lock (_gate)
        {
            _health = new IntegrationHealthTracker(configuration);
        }

        var cts = new CancellationTokenSource();
        var activation = _runtime.ActivateAsync(configuration, approval, cts.Token);

        // AgentRuntime decides NotTested before it starts watching anything, so a refused
        // activation has already completed here; an accepted one runs until cancelled.
        if (activation.IsCompleted)
        {
            cts.Dispose();
            var result = await activation.ConfigureAwait(false);
            if (result.Status == ActivationStatus.NotTested)
            {
                lock (_gate)
                {
                    _health = null;
                }

                RunOnDispatcher(NotifyHealthChanged);
                SetStatus(false, "Ativação recusada: esta configuração não passou no modo de teste ou foi alterada depois do último teste. Execute o teste e aprove-a novamente.");
            }

            return result.Status;
        }

        _activationCts = cts;
        _activation = activation;
        SetStatus(true, $"Monitorando '{configuration.Name}'. Aguardando a aplicação '{configuration.Application.ProcessIdentity}'.");
        _ = ObserveActivationAsync(activation);
        await RefreshEventsAsync().ConfigureAwait(false);
        return ActivationStatus.Activated;
    }

    /// <summary>Stops monitoring; every active session is closed before this returns.</summary>
    public async Task StopAsync()
    {
        var cts = _activationCts;
        var activation = _activation;
        if (cts is null || activation is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await activation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception)
        {
            // Already reported by ObserveActivationAsync.
        }
        finally
        {
            cts.Dispose();
            _activationCts = null;
            _activation = null;
        }

        SetStatus(false, "Monitoramento parado.");
    }

    /// <summary>Reloads <see cref="Events"/> from the outbox.</summary>
    public async Task RefreshEventsAsync(CancellationToken cancellationToken = default)
    {
        // Reads overlap when events are persisted in quick succession; a read that
        // started earlier must never replace the result of one that started later.
        var read = Interlocked.Increment(ref _eventReadsStarted);
        var pending = await _outbox.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        var attachmentNames = await DescribeAttachmentsAsync(pending, cancellationToken).ConfigureAwait(false);
        var events = pending.Select(domainEvent => ToDisplay(domainEvent, attachmentNames)).ToArray();
        lock (_gate)
        {
            if (read < _newestAppliedEventRead)
            {
                return;
            }

            _newestAppliedEventRead = read;
            _events = events;
            _capacity = _capacityPolicy.Evaluate(events.Length);
        }

        RunOnDispatcher(() =>
        {
            OnPropertyChanged(nameof(Events));
            OnPropertyChanged(nameof(CapacityAlert));
            OnPropertyChanged(nameof(IsCapacityCritical));
        });
    }

    private async Task ObserveActivationAsync(Task<ActivationResult> activation)
    {
        try
        {
            await activation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            AddDiagnostic(new DiagnosticDisplay(
                DateTimeOffset.UtcNow,
                true,
                $"O monitoramento parou por uma falha inesperada ({exception.GetType().Name}). Ative novamente."));
            SetStatus(false, "Monitoramento interrompido por falha.");
        }
    }

    private void OnDiagnosticPublished(object? sender, RuntimeDiagnostic diagnostic)
    {
        lock (_gate)
        {
            _health?.Apply(diagnostic);
        }

        RunOnDispatcher(NotifyHealthChanged);

        AddDiagnostic(new DiagnosticDisplay(
            diagnostic.Timestamp,
            diagnostic.Severity == RuntimeDiagnosticSeverity.Error,
            Describe(diagnostic)));

        if (diagnostic.Code == RuntimeDiagnosticCode.EventPersisted)
        {
            _ = RefreshEventsSafelyAsync();
        }
    }

    private async Task RefreshEventsSafelyAsync()
    {
        try
        {
            await RefreshEventsAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddDiagnostic(new DiagnosticDisplay(
                DateTimeOffset.UtcNow,
                true,
                $"Não foi possível ler a fila local de eventos ({exception.GetType().Name})."));
        }
    }

    /// <summary>
    /// Maps a diagnostic to operator text. Built only from IDs, codes and metadata -
    /// a <see cref="RuntimeDiagnostic"/> never carries a captured value.
    /// </summary>
    internal static string Describe(RuntimeDiagnostic diagnostic) => diagnostic.Code switch
    {
        RuntimeDiagnosticCode.TriggerWatchStarted =>
            $"Monitorando gatilho '{diagnostic.TriggerId}'.",
        RuntimeDiagnosticCode.TriggerWatchFailed =>
            $"O gatilho '{diagnostic.TriggerId}' não está sendo monitorado: o elemento não foi encontrado ou deixou de existir. Nova tentativa automática em andamento; se persistir, verifique a tela da aplicação e teste a configuração novamente.",
        RuntimeDiagnosticCode.EventPersisted =>
            $"Evento '{diagnostic.EventType}' gravado na fila local (gatilho '{diagnostic.TriggerId}').",
        RuntimeDiagnosticCode.SessionRejected =>
            $"Gatilho '{diagnostic.TriggerId}' rejeitado: {DescribeFailure(diagnostic.FailureCode, diagnostic.FieldId)} Nenhum evento foi gerado.",
        RuntimeDiagnosticCode.SessionIgnored =>
            $"Gatilho '{diagnostic.TriggerId}' ignorado: não pertence à etapa atual da sessão.",
        RuntimeDiagnosticCode.SelectorFallback =>
            $"Campo '{diagnostic.FieldId}' encontrado com confiança baixa ({diagnostic.Confidence:P0}). Revise o seletor.",
        RuntimeDiagnosticCode.SessionClosed =>
            "Aplicação fechada ou monitoramento parado: sessão encerrada.",
        _ => $"Diagnóstico {diagnostic.Code}.",
    };

    internal static string Describe(IntegrationHealth health)
    {
        if (health.State == IntegrationHealthState.Healthy)
        {
            return "Integração saudável.";
        }

        var reasons = string.Join(" ", health.Issues.Select(issue => issue.Kind switch
        {
            IntegrationIssueKind.FieldUnreadable =>
                $"Não foi possível ler o campo '{issue.FieldId}' (ausente, ambíguo ou ilegível): ajuste o seletor e teste novamente.",
            IntegrationIssueKind.TriggerUnwatchable =>
                $"O gatilho '{issue.TriggerId}' não pode ser monitorado.",
            IntegrationIssueKind.LowConfidenceMatch =>
                $"O campo '{issue.FieldId}' foi encontrado com confiança baixa ({issue.Confidence:P0}): revise o seletor.",
            _ => issue.Kind.ToString(),
        }));

        return health.State == IntegrationHealthState.Broken
            ? $"Integração quebrada. {reasons}"
            : $"Integração degradada. {reasons}";
    }

    private void NotifyHealthChanged()
    {
        OnPropertyChanged(nameof(HealthState));
        OnPropertyChanged(nameof(HealthText));
    }

    private static string DescribeFailure(SessionFailureCode? code, string? fieldId) => code switch
    {
        SessionFailureCode.MissingRequiredField => $"o campo obrigatório '{fieldId}' está vazio.",
        SessionFailureCode.CaptureFailed => $"não foi possível ler o campo '{fieldId}' (ausente, ambíguo ou ilegível).",
        null => "motivo desconhecido.",
        _ => $"falha {code}.",
    };

    /// <summary>Display text for every attachment the events reference (file name, size, origin) - never the raw reference.</summary>
    private async Task<IReadOnlyDictionary<string, string>> DescribeAttachmentsAsync(IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var references = events
            .SelectMany(domainEvent => domainEvent.Payload.Fields.Values)
            .Where(AttachmentReference.IsReference)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var info = _attachments is null ? null : await _attachments.GetInfoAsync(reference, cancellationToken).ConfigureAwait(false);
            descriptions[reference] = info is null
                ? "arquivo (indisponível)"
                : $"arquivo {info.FileName} ({Math.Max(1, (info.Size + 1023) / 1024)} KB, {(info.Source == AttachmentSource.File ? "cópia do arquivo" : "imagem da tela")})";
        }

        return descriptions;
    }

    private static EventDisplay ToDisplay(DomainEvent domainEvent, IReadOnlyDictionary<string, string> attachmentNames) => new(
        domainEvent.Sequence,
        domainEvent.Type,
        domainEvent.SessionId,
        domainEvent.Timestamp,
        domainEvent.Payload.Items.IsDefault ? 0 : domainEvent.Payload.Items.Length,
        string.Join("; ", domainEvent.Payload.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={(attachmentNames.TryGetValue(pair.Value, out var name) ? name : pair.Value)}")));

    private void AddDiagnostic(DiagnosticDisplay display)
    {
        lock (_gate)
        {
            _diagnostics.Add(display);
            if (_diagnostics.Count > MaxDiagnostics)
            {
                _diagnostics.RemoveAt(0);
            }
        }

        RunOnDispatcher(() => OnPropertyChanged(nameof(Diagnostics)));
    }

    private void SetStatus(bool isMonitoring, string statusText) =>
        RunOnDispatcher(() =>
        {
            _isMonitoring = isMonitoring;
            _statusText = statusText;
            OnPropertyChanged(nameof(IsMonitoring));
            OnPropertyChanged(nameof(StatusText));
        });

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
