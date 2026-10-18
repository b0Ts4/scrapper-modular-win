using System.Runtime.CompilerServices;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Runtime;

/// <summary>
/// One integration across two programs (plan 16): the session belongs to an instance of the
/// integration's main program; a field or button whose selector names another process is
/// resolved and watched in that process (<see cref="ApplicationInstance.AnyProcess"/>) instead
/// of in the main instance. Used by the runtime and by test mode alike.
/// </summary>
public static class ProcessRouting
{
    public static ISelectorResolver Resolver(string mainProcess, SelectorResolverFactory factory, ApplicationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(instance);
        return new RoutingResolver(mainProcess, factory(instance), new Lazy<ISelectorResolver>(() => factory(ApplicationInstance.AnyProcess)));
    }

    public static ITriggerProvider Triggers(string mainProcess, TriggerProviderFactory factory, ApplicationInstance instance, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(instance);
        return new RoutingTriggers(mainProcess, factory(instance, sessionId), new Lazy<ITriggerProvider>(() => factory(ApplicationInstance.AnyProcess, sessionId)));
    }

    /// <summary>The same program: names compared ignoring letter case and a trailing ".exe".</summary>
    public static bool SameProcess(string? first, string? second) =>
        string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    /// <summary>A selector without a process, or naming the main one, belongs to the main program.</summary>
    private static bool IsMain(string mainProcess, ElementFingerprint selector) =>
        string.IsNullOrWhiteSpace(selector.ProcessIdentity) || SameProcess(mainProcess, selector.ProcessIdentity);

    private static string Normalize(string? process)
    {
        var trimmed = (process ?? string.Empty).Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private sealed class RoutingResolver(string mainProcess, ISelectorResolver main, Lazy<ISelectorResolver> other) : ISelectorResolver
    {
        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken) =>
            IsMain(mainProcess, fingerprint)
                ? main.ResolveAsync(fingerprint, cancellationToken)
                : other.Value.ResolveAsync(fingerprint, cancellationToken);
    }

    private sealed class RoutingTriggers : ITriggerProvider
    {
        private readonly string _mainProcess;
        private readonly ITriggerProvider _main;
        private readonly Lazy<ITriggerProvider> _other;

        public RoutingTriggers(string mainProcess, ITriggerProvider main, Lazy<ITriggerProvider> other)
        {
            _mainProcess = mainProcess;
            _main = main;
            _other = other;
            _main.WatchEstablished += Forward;
        }

        public event EventHandler<string>? WatchEstablished;

        public async IAsyncEnumerable<TriggerSignal> WatchAsync(TriggerDefinition trigger, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(trigger);
            ITriggerProvider provider;
            if (IsMain(_mainProcess, trigger.Selector))
            {
                provider = _main;
            }
            else
            {
                var created = !_other.IsValueCreated;
                provider = _other.Value;
                if (created)
                {
                    provider.WatchEstablished += Forward;
                }
            }

            await foreach (var signal in provider.WatchAsync(trigger, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return signal;
            }
        }

        private void Forward(object? sender, string triggerId) => WatchEstablished?.Invoke(this, triggerId);
    }
}
