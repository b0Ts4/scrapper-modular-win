using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Domain.Sessions;

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

    private CancellationTokenSource? _activationCts;
    private Task<ActivationResult>? _activation;
    private bool _isMonitoring;
    private string _statusText = "Inativo.";

    public RuntimeMonitorViewModel(AgentRuntime runtime, IEventOutbox outbox, Dispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(outbox);

        _runtime = runtime;
        _outbox = outbox;
        _dispatcher = dispatcher;
        _runtime.DiagnosticPublished += OnDiagnosticPublished;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsMonitoring => _isMonitoring;

    public string StatusText => _statusText;

    /// <summary>The most recent diagnostics, oldest first (bounded).</summary>
    public IReadOnlyList<DiagnosticDisplay> Diagnostics { get; private set; } = Array.Empty<DiagnosticDisplay>();

    /// <summary>Every pending event in the outbox, in append order.</summary>
    public IReadOnlyList<EventDisplay> Events { get; private set; } = Array.Empty<EventDisplay>();

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
        var pending = await _outbox.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        var events = pending.Select(ToDisplay).ToArray();
        RunOnDispatcher(() =>
        {
            Events = events;
            OnPropertyChanged(nameof(Events));
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

    private static string DescribeFailure(SessionFailureCode? code, string? fieldId) => code switch
    {
        SessionFailureCode.MissingRequiredField => $"o campo obrigatório '{fieldId}' está vazio.",
        SessionFailureCode.CaptureFailed => $"não foi possível ler o campo '{fieldId}' (ausente, ambíguo ou ilegível).",
        null => "motivo desconhecido.",
        _ => $"falha {code}.",
    };

    private static EventDisplay ToDisplay(DomainEvent domainEvent) => new(
        domainEvent.Sequence,
        domainEvent.Type,
        domainEvent.SessionId,
        domainEvent.Timestamp,
        domainEvent.Payload.Items.IsDefault ? 0 : domainEvent.Payload.Items.Length,
        string.Join("; ", domainEvent.Payload.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")));

    private void AddDiagnostic(DiagnosticDisplay display)
    {
        DiagnosticDisplay[] snapshot;
        lock (_gate)
        {
            _diagnostics.Add(display);
            if (_diagnostics.Count > MaxDiagnostics)
            {
                _diagnostics.RemoveAt(0);
            }

            snapshot = _diagnostics.ToArray();
        }

        RunOnDispatcher(() =>
        {
            Diagnostics = snapshot;
            OnPropertyChanged(nameof(Diagnostics));
        });
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
