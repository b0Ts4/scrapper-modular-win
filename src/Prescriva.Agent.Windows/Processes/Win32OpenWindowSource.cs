using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Prescriva.Agent.Application.Inspection;

namespace Prescriva.Agent.Windows.Processes;

/// <summary>
/// The open programs a person can pick from: visible, titled, unowned top-level windows that are
/// not cloaked (a suspended Store app's hidden frame is) and not tool windows, excluding the
/// Agent's own process. Store/UWP apps are framed by <c>ApplicationFrameHost</c>; for those the
/// listed process is the one that owns the content (e.g. <c>CalculatorApp</c>), which is the
/// process the integration identifies. Reads only window titles and process names - never
/// window content - and keeps nothing.
/// </summary>
public sealed class Win32OpenWindowSource : IOpenWindowSource
{
    public const string FrameHostProcessName = "ApplicationFrameHost";

    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const int DwmwaCloaked = 14;

    private readonly int _ownProcessId;

    public Win32OpenWindowSource(int? ownProcessId = null)
    {
        _ownProcessId = ownProcessId ?? Environment.ProcessId;
    }

    public Task<IReadOnlyList<OpenWindowInfo>> ListAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<OpenWindowInfo>>(() => List(cancellationToken), cancellationToken);

    private List<OpenWindowInfo> List(CancellationToken cancellationToken)
    {
        var handles = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            handles.Add(handle);
            return true;
        }, IntPtr.Zero);

        var windows = new List<OpenWindowInfo>();
        var names = new Dictionary<int, (string Process, string App)?>();
        foreach (var handle in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCandidate(handle))
            {
                continue;
            }

            var title = TitleOf(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            GetWindowThreadProcessId(handle, out var windowProcessId);
            var processId = (int)windowProcessId;
            var described = Describe(processId, names);
            if (described is null)
            {
                continue;
            }

            if (string.Equals(described.Value.Process, FrameHostProcessName, StringComparison.OrdinalIgnoreCase))
            {
                // A Store app: the frame belongs to ApplicationFrameHost, the content to the app.
                var contentProcessId = ContentProcessOf(handle, processId);
                if (contentProcessId is null)
                {
                    continue; // an empty frame (app still starting or suspended)
                }

                processId = contentProcessId.Value;
                described = Describe(processId, names);
                if (described is null)
                {
                    continue;
                }
            }

            if (processId == _ownProcessId)
            {
                continue;
            }

            windows.Add(new OpenWindowInfo(described.Value.App, title, described.Value.Process, processId));
        }

        return windows;
    }

    /// <summary>The PID of the first child window owned by another process than the frame host.</summary>
    internal static int? ContentProcessOf(IntPtr frame, int frameProcessId)
    {
        int? content = null;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childProcessId);
            if ((int)childProcessId != frameProcessId)
            {
                content = (int)childProcessId;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return content;
    }

    private static bool IsCandidate(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero)
        {
            return false;
        }

        if ((GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
        {
            return false;
        }

        return DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private static string TitleOf(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static (string Process, string App)? Describe(int processId, Dictionary<int, (string Process, string App)?> cache)
    {
        if (cache.TryGetValue(processId, out var known))
        {
            return known;
        }

        (string Process, string App)? described = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            var app = name;
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    app = description.Trim();
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Another user's or a protected process: the process name is enough.
            }

            described = (name, app);
        }
        catch (ArgumentException)
        {
            // The process exited while listing.
        }

        cache[processId] = described;
        return described;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
