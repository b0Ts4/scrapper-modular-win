using System.Threading;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.Tests.Automation;

public sealed class AutomationDispatcherTests
{
    [Fact]
    public async Task RunAsync_executes_the_operation_on_a_dedicated_STA_thread()
    {
        using var dispatcher = new AutomationDispatcher();

        var (threadId, apartmentState) = await dispatcher.RunAsync(
            _ => (Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState()),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(ApartmentState.STA, apartmentState);
        Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, threadId);
    }

    [Fact]
    public async Task RunAsync_always_marshals_work_onto_the_same_background_thread()
    {
        using var dispatcher = new AutomationDispatcher();

        var first = await dispatcher.RunAsync(_ => Thread.CurrentThread.ManagedThreadId, TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = await dispatcher.RunAsync(_ => Thread.CurrentThread.ManagedThreadId, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task RunAsync_times_out_and_reports_TimedOut_when_the_operation_runs_too_long()
    {
        using var dispatcher = new AutomationDispatcher();

        var failure = await Assert.ThrowsAsync<ElementInspectionFailure>(() => dispatcher.RunAsync(
            _ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return true;
            },
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None));

        Assert.Equal(ElementInspectionFailureKind.TimedOut, failure.Kind);
    }

    [Fact]
    public async Task RunAsync_cancellation_prevents_queued_work_from_ever_running()
    {
        using var dispatcher = new AutomationDispatcher();
        using var blockerStarted = new ManualResetEventSlim(false);
        using var releaseBlocker = new ManualResetEventSlim(false);

        // Occupy the single worker thread so the next request stays queued, unstarted.
        var blockingTask = dispatcher.RunAsync(
            _ =>
            {
                blockerStarted.Set();
                releaseBlocker.Wait(TimeSpan.FromSeconds(10));
                return true;
            },
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5)), "Blocking operation never started.");

        using var cts = new CancellationTokenSource();
        var executed = false;
        var queuedTask = dispatcher.RunAsync(
            _ =>
            {
                executed = true;
                return true;
            },
            TimeSpan.FromSeconds(10),
            cts.Token);

        cts.Cancel();

        var failure = await Assert.ThrowsAsync<ElementInspectionFailure>(() => queuedTask);
        Assert.Equal(ElementInspectionFailureKind.Cancelled, failure.Kind);

        releaseBlocker.Set();
        Assert.True(await blockingTask);

        Assert.False(executed, "The cancelled operation must never run, even after it is dequeued.");
    }

    [Fact]
    public async Task RunAsync_propagates_unexpected_exceptions_as_an_ElementUnavailable_failure()
    {
        using var dispatcher = new AutomationDispatcher();

        var failure = await Assert.ThrowsAsync<ElementInspectionFailure>(() => dispatcher.RunAsync<bool>(
            _ => throw new InvalidOperationException("boom"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None));

        Assert.Equal(ElementInspectionFailureKind.ElementUnavailable, failure.Kind);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public async Task Every_call_completes_even_when_its_deadline_expires_while_it_is_being_dequeued()
    {
        // Regression: with an (effectively) immediate timeout, the deadline could flip to
        // "cancelled" just before the queued item ran; the item then returned without
        // completing the task and disposed the registration whose callback would have -
        // leaving the caller awaiting forever (the x86 CI hang in ElementInspectionTests).
        using var dispatcher = new AutomationDispatcher();
        for (var i = 0; i < 20_000; i++)
        {
            var call = dispatcher.RunAsync(_ => i, TimeSpan.Zero, CancellationToken.None);
            try
            {
                await call.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Call {i} never completed.");
            }
            catch (ElementInspectionFailure failure)
            {
                Assert.Equal(ElementInspectionFailureKind.TimedOut, failure.Kind);
            }
        }
    }
}
