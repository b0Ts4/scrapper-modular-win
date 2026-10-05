using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Prescriva.Agent.Desktop.Shell;

/// <summary>
/// One Agent per data directory (per user session): a named mutex marks the running
/// instance, and a named event lets a second start ask it to show its window. Names derive
/// from the data directory, so isolated test directories never collide.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequested;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequested, RegisteredWaitHandle registration)
    {
        _mutex = mutex;
        _showRequested = showRequested;
        _registration = registration;
    }

    /// <summary>Becomes the running instance, or returns null when one already runs for this data directory.</summary>
    public static SingleInstance? TryAcquire(string dataDirectory, Action onShowRequested)
    {
        var name = NameFor(dataDirectory);
        var mutex = new Mutex(initiallyOwned: false, name + "-instance");
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous instance was killed (power loss, task manager): ownership is ours.
        }

        var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
        var registration = ThreadPool.RegisterWaitForSingleObject(
            showRequested, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
        return new SingleInstance(mutex, showRequested, registration);
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public static void SignalShow(string dataDirectory)
    {
        if (EventWaitHandle.TryOpenExisting(NameFor(dataDirectory) + "-show", out var showRequested))
        {
            using (showRequested)
            {
                showRequested.Set();
            }
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _showRequested.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; the handle is closed below and released on exit anyway.
        }

        _mutex.Dispose();
    }

    private static string NameFor(string dataDirectory)
    {
        var normalized = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return @"Local\PrescrivaAgent-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }
}
