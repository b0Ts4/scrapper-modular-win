using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Native identity of a child window for programs whose UI Automation AutomationId is the
/// window handle (Delphi VCL gives every child created without a control ID its own handle
/// as ID, so it changes on every start). The window path is the chain of window classes from
/// the top-level window down to the control, each with its index among same-class siblings
/// (<c>TPanel[0]/TPageControl[1]/TEdit[3]</c>); VCL creates child windows in a fixed order, so
/// the path is the same on every start. Reads only class names, handles and positions -
/// never window text, except a control's caption when a selector asks for its name.
/// </summary>
internal static class Win32WindowPath
{
    private const int GaParent = 1;
    private const int GwHwndNext = 2;
    private const int GwChild = 5;

    /// <summary>True when <paramref name="automationId"/> is just the element's own window handle.</summary>
    public static bool IsVolatileAutomationId(string? automationId, int nativeWindowHandle) =>
        nativeWindowHandle != 0 &&
        string.Equals(automationId, nativeWindowHandle.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The path of a child window below its top-level window, or null for a top-level window.</summary>
    public static string? Of(IntPtr handle)
    {
        var segments = new List<string>();
        var desktop = GetDesktopWindow();
        var current = handle;
        while (true)
        {
            var parent = GetAncestor(current, GaParent);
            if (parent == IntPtr.Zero || parent == desktop)
            {
                break;
            }

            segments.Add(Segment(parent, current));
            current = parent;
        }

        if (segments.Count == 0)
        {
            return null;
        }

        segments.Reverse();
        return string.Join('/', segments);
    }

    /// <summary>Every child window below <paramref name="topLevel"/>, with its class and path, in creation order.</summary>
    public static IReadOnlyList<Win32Descendant> Descendants(IntPtr topLevel)
    {
        var result = new List<Win32Descendant>();
        Collect(topLevel, string.Empty, result);
        return result;
    }

    public static (double X, double Y, double Width, double Height)? Bounds(IntPtr handle) =>
        GetWindowRect(handle, out var rect) ? (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : null;

    public static string Text(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static void Collect(IntPtr parent, string parentPath, List<Win32Descendant> result)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var child = GetWindow(parent, GwChild); child != IntPtr.Zero; child = GetWindow(child, GwHwndNext))
        {
            var className = ClassOf(child);
            seen.TryGetValue(className, out var index);
            seen[className] = index + 1;

            var path = Join(parentPath, Format(className, index));
            result.Add(new Win32Descendant(child, className, path));
            Collect(child, path, result);
        }
    }

    private static string Segment(IntPtr parent, IntPtr child)
    {
        var className = ClassOf(child);
        var index = 0;
        for (var sibling = GetWindow(parent, GwChild); sibling != IntPtr.Zero && sibling != child; sibling = GetWindow(sibling, GwHwndNext))
        {
            if (string.Equals(ClassOf(sibling), className, StringComparison.Ordinal))
            {
                index++;
            }
        }

        return Format(className, index);
    }

    private static string Format(string className, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{className}[{index}]");

    private static string Join(string parentPath, string segment) =>
        parentPath.Length == 0 ? segment : parentPath + "/" + segment;

    private static string ClassOf(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);
}

/// <summary>A child window found by <see cref="Win32WindowPath.Descendants"/>.</summary>
internal sealed record Win32Descendant(IntPtr Handle, string ClassName, string Path);
