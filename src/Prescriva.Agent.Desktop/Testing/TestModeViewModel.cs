using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Desktop.Testing;

/// <summary>Everything the view needs to render one field's test result - already in display-ready form.</summary>
/// <param name="StatusText">A short Portuguese status label ("Encontrado", "Não encontrado", "Ambíguo").</param>
/// <param name="ProviderText">The serving provider ID, or "-" when the field did not resolve.</param>
/// <param name="ConfidenceText">The confidence as a percentage, or "-" when the field did not resolve.</param>
/// <param name="ValueText">
/// The value read from the live field during this run (in memory only, never logged or
/// persisted), or "-" when the field did not resolve or produced no value.
/// </param>
/// <param name="FailureMessage">
/// Actionable Portuguese text for the operator when the field failed, or null when it
/// passed. Built purely from <see cref="FieldCheckResult.FailureCode"/> - never from any
/// captured value - matching the same privacy discipline <c>ITechnicalLog</c> entries keep.
/// </param>
public sealed record FieldResultDisplay(
    string FieldId,
    bool Passed,
    string StatusText,
    string ProviderText,
    string ConfidenceText,
    string? FailureMessage,
    string ValueText = "-")
{
    /// <summary>Matched selector signals with their weights, strongest first, and the lead over the next candidate ("-" when none).</summary>
    public string SignalsText { get; init; } = "-";

    /// <summary>How long resolving and reading the field took.</summary>
    public string DurationText { get; init; } = "-";

    /// <summary>Fragility warnings in Portuguese; empty when the selector looks robust.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Everything the view needs to render one trigger's test result - already in display-ready form.</summary>
public sealed record TriggerResultDisplay(
    string TriggerId,
    bool Passed,
    string StatusText,
    string? FailureMessage,
    IReadOnlyList<string> StageTransitions,
    IReadOnlyList<string> EmittedEventTypes)
{
    /// <summary>How long the trigger took to be detected.</summary>
    public string DurationText { get; init; } = "-";

    /// <summary>What the trigger would do: stage transitions and emitted events ("-" when nothing).</summary>
    public string EffectsText { get; init; } = "-";
}

/// <summary>
/// Wires <see cref="IntegrationTestRunner"/> into something a WPF view can bind to: runs
/// the test suite for a configuration, exposes its field/trigger results in a
/// display-ready form with actionable Portuguese failure text, and - only once every check
/// has passed - lets the operator approve the configuration, producing a
/// <see cref="ConfigurationApproval"/> bound to the exact content that was just proven to
/// work.
///
/// Holds no UI Automation reference and no XAML dependency of its own - plain
/// INotifyPropertyChanged over IntegrationTestRunner's already-plain
/// <see cref="IntegrationTestReport"/>, following the same pattern <c>InspectorViewModel</c>
/// already established. The captured report (including any field <see cref="FieldCheckResult.Value"/>)
/// lives only in this view model's in-memory <see cref="Report"/> property - nothing here
/// ever writes it to a configuration store or event outbox; only an explicit save action a
/// host view wires up (outside this class) could ever persist it.
/// </summary>
public sealed class TestModeViewModel : INotifyPropertyChanged
{
    private readonly IntegrationTestRunner _runner;
    private readonly Dispatcher? _dispatcher;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ApprovalService? _approvals;

    private IntegrationTestReport? _report;
    private bool _isRunning;
    private ConfigurationApproval? _lastApproval;
    private string? _gateMessage;

    /// <param name="approvals">
    /// Where <see cref="ApproveAsync"/> records approvals so they survive an Agent restart.
    /// Without it, approvals are kept in memory only.
    /// </param>
    public TestModeViewModel(
        IntegrationTestRunner runner,
        Dispatcher? dispatcher = null,
        Func<DateTimeOffset>? clock = null,
        ApprovalService? approvals = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        _dispatcher = dispatcher;
        _approvals = approvals;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The most recent test run's raw report, or null before any run.</summary>
    public IntegrationTestReport? Report => _report;

    /// <summary>True while <see cref="RunAsync"/> is in flight.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Display-ready results, one per configured field in the most recent run.</summary>
    public IReadOnlyList<FieldResultDisplay> FieldResults { get; private set; } = Array.Empty<FieldResultDisplay>();

    /// <summary>Display-ready results, one per configured trigger in the most recent run.</summary>
    public IReadOnlyList<TriggerResultDisplay> TriggerResults { get; private set; } = Array.Empty<TriggerResultDisplay>();

    /// <summary>True only when a report exists and every field and trigger in it passed.</summary>
    public bool CanApprove => _report?.AllPassed == true;

    /// <summary>The approval produced by the most recent successful <see cref="Approve"/> call, or null.</summary>
    public ConfigurationApproval? LastApproval => _lastApproval;

    /// <summary>
    /// Set by a host that already knows activation was refused with
    /// <c>ActivationStatus.NotTested</c> (e.g. after a failed <c>AgentRuntime.ActivateAsync</c>
    /// call) - a Portuguese message the view can surface directly, explaining why activation
    /// was refused, independent of whatever this view model's own last run (if any) showed.
    /// </summary>
    public string? GateMessage
    {
        get => _gateMessage;
        set
        {
            _gateMessage = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Runs <see cref="IntegrationTestRunner.RunAsync"/> for <paramref name="configuration"/>
    /// and republishes its report in display-ready form. A prior <see cref="LastApproval"/>
    /// is cleared - a new run means the previous approval decision no longer speaks for the
    /// configuration being tested right now.
    /// </summary>
    public async Task RunAsync(IntegrationConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        SetIsRunning(true);
        try
        {
            var report = await _runner.RunAsync(configuration, cancellationToken).ConfigureAwait(false);
            ApplyReport(report);
        }
        finally
        {
            SetIsRunning(false);
        }
    }

    /// <summary>
    /// Approves the configuration the most recent report was run against, binding the
    /// approval to that report's fingerprint. Throws <see cref="InvalidOperationException"/>
    /// when there is no report yet, or when <see cref="CanApprove"/> is false (any field or
    /// trigger failed) - a failed test run can never be approved, by construction rather
    /// than by a caller remembering to check first.
    /// </summary>
    public ConfigurationApproval Approve()
    {
        var report = _report
            ?? throw new InvalidOperationException("Run the test suite (call RunAsync) before approving.");

        var approval = report.ToApproval(_clock())
            ?? throw new InvalidOperationException("Cannot approve: the most recent test run did not pass every check.");

        RunOnDispatcher(() =>
        {
            _lastApproval = approval;
            OnPropertyChanged(nameof(LastApproval));
        });

        return approval;
    }

    /// <summary>
    /// Like <see cref="Approve"/>, and also records the approval through the
    /// <see cref="ApprovalService"/> (when one was supplied) so it survives a restart. A
    /// failed run throws before anything is recorded.
    /// </summary>
    public async Task<ConfigurationApproval> ApproveAsync(CancellationToken cancellationToken = default)
    {
        var report = _report
            ?? throw new InvalidOperationException("Run the test suite (call RunAsync) before approving.");
        if (!report.AllPassed)
        {
            throw new InvalidOperationException("Cannot approve: the most recent test run did not pass every check.");
        }

        var approval = _approvals is null
            ? Approve()
            : await _approvals.RecordAsync(report, cancellationToken).ConfigureAwait(false);

        RunOnDispatcher(() =>
        {
            _lastApproval = approval;
            OnPropertyChanged(nameof(LastApproval));
        });

        return approval;
    }

    private void ApplyReport(IntegrationTestReport report)
    {
        var fieldResults = report.FieldResults.Select(ToDisplay).ToArray();
        var triggerResults = report.TriggerResults.Select(ToDisplay).ToArray();

        RunOnDispatcher(() =>
        {
            _report = report;
            _lastApproval = null;
            FieldResults = fieldResults;
            TriggerResults = triggerResults;

            OnPropertyChanged(nameof(Report));
            OnPropertyChanged(nameof(FieldResults));
            OnPropertyChanged(nameof(TriggerResults));
            OnPropertyChanged(nameof(CanApprove));
            OnPropertyChanged(nameof(LastApproval));
        });
    }

    public static FieldResultDisplay ToDisplay(FieldCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var passed = result.Outcome == FieldCheckOutcome.Found;
        var statusText = result.Outcome switch
        {
            FieldCheckOutcome.Found => "Encontrado",
            FieldCheckOutcome.Ambiguous => "Ambíguo",
            FieldCheckOutcome.Unreadable => "Ilegível",
            _ => "Não encontrado",
        };

        return new FieldResultDisplay(
            result.FieldId,
            passed,
            statusText,
            passed ? result.ProviderId ?? "-" : "-",
            passed ? $"{result.Confidence:P0}" : "-",
            passed ? null : DescribeFieldFailure(result.FailureCode),
            !passed ? "-"
                : result.Attachment is { } attachment ? DescribeAttachment(attachment)
                : !string.IsNullOrEmpty(result.Value) ? result.Value : "-")
        {
            SignalsText = DescribeSignals(result.Signals, result.Lead),
            DurationText = DescribeDuration(result.Duration),
            Warnings = result.Warnings.IsDefaultOrEmpty ? [] : result.Warnings.Select(DescribeWarning).ToArray(),
        };
    }

    public static TriggerResultDisplay ToDisplay(TriggerCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var passed = result.Outcome == TriggerCheckOutcome.Detected;
        var statusText = passed ? "Detectado" : "Tempo esgotado";

        return new TriggerResultDisplay(
            result.TriggerId,
            passed,
            statusText,
            passed ? null : DescribeTriggerFailure(result.FailureCode),
            result.StageTransitions,
            result.EmittedEventTypes)
        {
            DurationText = passed ? DescribeDuration(result.Duration) : "-",
            EffectsText = DescribeEffects(result.StageTransitions, result.EmittedEventTypes),
        };
    }

    private static readonly Dictionary<string, string> SignalNames = new(StringComparer.Ordinal)
    {
        ["automationId"] = "AutomationId",
        ["nearbyLabels"] = "rótulo",
        ["controlType"] = "tipo",
        ["name"] = "nome",
        ["ancestors"] = "estrutura",
        ["className"] = "classe",
        ["relativeBounds"] = "posição",
        ["frameworkId"] = "framework",
    };

    private static string DescribeSignals(IReadOnlyDictionary<string, int>? signals, int? lead)
    {
        if (signals is null || signals.Count == 0)
        {
            return "-";
        }

        var text = string.Join(", ", signals
            .OrderByDescending(signal => signal.Value)
            .ThenBy(signal => signal.Key, StringComparer.Ordinal)
            .Select(signal => $"{SignalNames.GetValueOrDefault(signal.Key, signal.Key)} ({signal.Value})"));
        return lead is { } margin ? $"{text}; margem {margin}" : text;
    }

    private static string DescribeDuration(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? "-" : $"{Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero):0} ms";

    private static string DescribeEffects(IReadOnlyList<string> transitions, IReadOnlyList<string> events)
    {
        var parts = transitions.Select(stage => $"etapa → {stage}").Concat(events.Select(type => $"evento {type}")).ToArray();
        return parts.Length == 0 ? "-" : string.Join("; ", parts);
    }

    private static string DescribeWarning(SelectorFragilityWarning warning) => warning switch
    {
        SelectorFragilityWarning.MissingAutomationId =>
            "Sem AutomationId: o campo é reconhecido pelo rótulo, nome ou estrutura, que a aplicação pode mudar.",
        SelectorFragilityWarning.LowConfidence =>
            "Confiança abaixo de 80%: parte dos sinais gravados não corresponde mais. Considere selecionar o campo novamente.",
        SelectorFragilityWarning.NarrowLead =>
            "Pouca margem sobre outro elemento parecido: uma pequena mudança na tela pode tornar a seleção ambígua.",
        SelectorFragilityWarning.PositionDependent =>
            "Reconhecido só pela posição e estrutura: mudanças de layout podem quebrar a seleção.",
        _ => warning.ToString(),
    };

    /// <summary>
    /// Maps a stable, value-free failure code to actionable Portuguese text for the
    /// operator - the UI surface this task's "render failure codes with actionable
    /// Portuguese text" rule targets. Technical logs never go through here: they keep only
    /// the code itself plus IDs/metadata (see <see cref="Application.Diagnostics.ITechnicalLog"/>).
    /// </summary>
    /// <summary>How a captured file is shown to the operator: name, size, and whether it is the original file or the on-screen image.</summary>
    public static string DescribeAttachment(CapturedAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var kilobytes = Math.Max(1, (attachment.Content.LongLength + 1023) / 1024);
        var origin = attachment.Source == AttachmentSource.File ? "cópia do arquivo" : "imagem da tela";
        return $"arquivo {attachment.FileName} ({kilobytes} KB, {origin})";
    }

    private static string DescribeFieldFailure(string? failureCode) => failureCode switch
    {
        FieldCheckResult.FieldUnreadableCode =>
            "Campo encontrado, mas o valor não pôde ser lido: arquivo inexistente ou acima de 10 MB, imagem coberta por outra janela, ou controle sem conteúdo. Corrija e teste novamente.",
        FieldCheckResult.FieldNotFoundCode =>
            "Campo não encontrado. Verifique se o elemento ainda está visível na tela e se o seletor configurado continua correto.",
        FieldCheckResult.FieldAmbiguousCode =>
            "Seleção ambígua: mais de um elemento corresponde a este campo. Ajuste o seletor para torná-lo mais específico.",
        _ => "Falha desconhecida ao resolver o campo. Verifique a configuração e tente novamente.",
    };

    private static string DescribeTriggerFailure(string? failureCode) => failureCode switch
    {
        TriggerCheckResult.TriggerTimedOutCode =>
            "Gatilho não detectado dentro do tempo limite. Realize a ação esperada na aplicação e execute o teste novamente.",
        _ => "Falha desconhecida ao aguardar o gatilho. Verifique a configuração e tente novamente.",
    };

    private void SetIsRunning(bool isRunning)
    {
        RunOnDispatcher(() =>
        {
            _isRunning = isRunning;
            OnPropertyChanged(nameof(IsRunning));
        });
    }

    /// <summary>
    /// Marshals a state update onto the UI dispatcher a WPF host supplied. Without one
    /// (headless use, e.g. tests) updates are applied inline: defaulting to the
    /// constructing thread's dispatcher would deadlock whenever that thread is not pumping
    /// messages while an awaited runner continuation calls <see cref="Dispatcher.Invoke(Action)"/>.
    /// </summary>
    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
