using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests;

/// <summary>
/// Launches the real, compiled Prescriva.Agent.TestTarget.exe and hands back its
/// top-level UI Automation element, so tests exercise a live desktop window instead
/// of a mock.
/// </summary>
internal sealed class TestTargetLauncher : IDisposable
{
    private readonly Process _process;

    private TestTargetLauncher(Process process, AutomationElement window)
    {
        _process = process;
        Window = window;
    }

    public AutomationElement Window { get; }

    public static TestTargetLauncher Launch(string layoutVariant = "default", TimeSpan? timeout = null)
    {
        var executablePath = ResolveExecutablePath();
        var startInfo = new ProcessStartInfo(executablePath, $"--layout-variant {layoutVariant}")
        {
            UseShellExecute = false,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{executablePath}'.");

        try
        {
            // Generous: the first start of a freshly built executable on a CI runner (JIT,
            // antivirus scan of the new binary) has exceeded 15 s; success returns at once.
            var window = WaitForMainWindow(process, timeout ?? TimeSpan.FromSeconds(45));
            return new TestTargetLauncher(process, window);
        }
        catch
        {
            KillQuietly(process);
            throw;
        }
    }

    public void Dispose()
    {
        KillQuietly(_process);
    }

    private static AutomationElement WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Prescriva.Agent.TestTarget exited early with code {process.ExitCode}.");
            }

            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                var element = AutomationElement.FromHandle(handle);
                if (element is not null)
                {
                    return element;
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("Timed out waiting for the Prescriva.Agent.TestTarget main window.");
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort cleanup; nothing more we can do if this fails.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// The TestTarget to launch: PRESCRIVA_TESTTARGET_EXE when set (CI uses it to run the
    /// suite against a 32-bit build), otherwise the solution's own build output.
    /// </summary>
    internal static string ResolveExecutablePath()
    {
        var overridden = Environment.GetEnvironmentVariable("PRESCRIVA_TESTTARGET_EXE");
        if (string.IsNullOrEmpty(overridden))
        {
            return ResolveBuiltExecutablePath();
        }

        if (!File.Exists(overridden))
        {
            throw new FileNotFoundException("PRESCRIVA_TESTTARGET_EXE does not point to an existing file.", overridden);
        }

        return overridden;
    }

    /// <summary>The TestTarget produced by building the solution (src/.../bin/{configuration}/net10.0-windows).</summary>
    internal static string ResolveBuiltExecutablePath()
    {
        var repoRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var configuration = FindConfigurationSegment(AppContext.BaseDirectory);

        var executablePath = Path.Combine(
            repoRoot,
            "src",
            "Prescriva.Agent.TestTarget",
            "bin",
            configuration,
            "net10.0-windows",
            "Prescriva.Agent.TestTarget.exe");

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                $"Could not find the built TestTarget executable. Expected it at '{executablePath}'. " +
                "Build Prescriva.Agent.TestTarget before running this test.",
                executablePath);
        }

        return executablePath;
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Prescriva.Agent.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate Prescriva.Agent.slnx above '{startDirectory}'.");
    }

    private static string FindConfigurationSegment(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (string.Equals(directory.Parent?.Name, "bin", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Name;
            }

            directory = directory.Parent;
        }

        // Fall back to the build configuration of this test assembly.
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}
