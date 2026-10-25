using System.Collections.Immutable;

namespace Prescriva.Agent.Application.Playbooks;

/// <summary>
/// Types into and reads from one program's window, the way a person at the keyboard would
/// (plan 19). Paths are window paths below the program's window; a dialog's field path is below
/// the dialog. Implemented for Win32/Delphi programs by the Windows project.
/// </summary>
public interface IPlaybookDriver
{
    /// <summary>Finds the program's window (process + window rule); false when it is not open.</summary>
    Task<bool> AttachAsync(string processIdentity, string windowRule, CancellationToken cancellationToken);

    /// <summary>Focuses the control, replaces its text with <paramref name="text"/> and leaves it with Tab.</summary>
    Task<WriteOutcome> SetTextAsync(string path, string text, CancellationToken cancellationToken);

    /// <summary>The control's text; null when the control is not there.</summary>
    Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Focuses the control and types <paramref name="text"/> (for a grid: into its current cell).</summary>
    Task<WriteOutcome> TypeAsync(string path, string text, CancellationToken cancellationToken);

    Task<WriteOutcome> PressKeyAsync(string path, PlaybookKey key, CancellationToken cancellationToken);

    /// <summary>Presses Alt+&lt;key&gt; on the program window (a menu/toolbar accelerator); no control path.</summary>
    Task<WriteOutcome> PressAcceleratorAsync(string key, CancellationToken cancellationToken);

    /// <summary>A button's caption; null when the control is not there.</summary>
    Task<string?> CaptionAsync(string path, CancellationToken cancellationToken);

    Task<WriteOutcome> ClickAsync(string path, CancellationToken cancellationToken);

    /// <summary>The program's other visible top-level windows, except those of <paramref name="ignoredClasses"/>.</summary>
    Task<IReadOnlyList<OpenDialog>> DialogsAsync(IEnumerable<string> ignoredClasses, CancellationToken cancellationToken);

    /// <summary>The text shown in the dialog (read from its image); null when it cannot be read.</summary>
    Task<string?> DialogTextAsync(OpenDialog dialog, CancellationToken cancellationToken);

    /// <summary>Presses the dialog's button with this caption; false when there is none.</summary>
    Task<bool> PressDialogButtonAsync(OpenDialog dialog, string caption, CancellationToken cancellationToken);

    /// <summary>The text of a control inside the dialog; null when there is none.</summary>
    Task<string?> ReadDialogFieldAsync(OpenDialog dialog, string path, CancellationToken cancellationToken);

    /// <summary>Replaces the text of a control inside the dialog; false when there is none.</summary>
    Task<bool> SetDialogTextAsync(OpenDialog dialog, string path, string text, CancellationToken cancellationToken);
}

public enum WriteOutcome
{
    Done,
    ControlNotFound,

    /// <summary>Nothing was typed: the program did not take the focus in time - typically a dialog opened from the focus change.</summary>
    Blocked,

    /// <summary>Part of the input may have been taken when the program stopped answering (a dialog opened while typing): it must not be typed again.</summary>
    Interrupted,
}

public enum PlaybookKey
{
    Enter,
    Tab,
}

/// <summary>A top-level window of the program other than its main one, with its button captions.</summary>
public sealed record OpenDialog(long Handle, string ClassName, string Title, ImmutableArray<string> Buttons);
