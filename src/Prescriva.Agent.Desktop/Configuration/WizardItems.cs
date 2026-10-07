using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Desktop.Configuration;

/// <summary>An open program in the step-1 list: its name, title, process and PID, and its icon when readable.</summary>
internal sealed record OpenWindowItem(OpenWindowInfo Info, ImageSource? Icon)
{
    public string Details => $"Processo {Info.ProcessName} · PID {Info.ProcessId}";

    /// <summary>Also the list item's accessible name.</summary>
    public override string ToString() => $"{Info.AppName} — {Info.WindowTitle} ({Info.ProcessName}, PID {Info.ProcessId})";

    /// <summary>
    /// Reads the executable's icon off the UI thread (frozen, so WPF may use it anywhere);
    /// null for a process the user may not inspect. Only the icon is read - never window content.
    /// </summary>
    public static OpenWindowItem Create(OpenWindowInfo info)
    {
        ImageSource? icon = null;
        try
        {
            using var process = Process.GetProcessById(info.ProcessId);
            var path = process.MainModule?.FileName;
            if (path is not null)
            {
                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(extracted.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    icon = source;
                }
            }
        }
        catch (System.Exception exception) when (exception is System.ComponentModel.Win32Exception or System.InvalidOperationException
            or System.ArgumentException or System.IO.IOException or System.NotSupportedException or System.UnauthorizedAccessException)
        {
            // Another user's or a protected process: listed without an icon.
        }

        return new OpenWindowItem(info, icon);
    }
}

/// <summary>A configured field in the step-2 list.</summary>
internal sealed record FieldItem(FieldDefinition Field)
{
    public override string ToString() =>
        $"{Field.Meaning} ({Field.Id}) · {(Field.Required ? "obrigatório" : "opcional")} · {Field.Kind switch
        {
            FieldKind.File => "arquivo / imagem",
            FieldKind.OcrText => "texto via OCR",
            _ => "texto",
        }}";
}

/// <summary>A configured button (trigger) in the step-3 list, described in words.</summary>
internal sealed record TriggerItem(TriggerDefinition Trigger, IReadOnlyDictionary<string, string> FieldNames)
{
    public override string ToString()
    {
        var parts = Trigger.Actions.Select(action => action switch
        {
            CaptureFieldsAction capture => "lê " + string.Join(", ", capture.FieldIds.Select(id => FieldNames.TryGetValue(id, out var name) ? name : id)),
            EmitEventAction emit => $"gera '{emit.EventType}'",
            TransitionStageAction transition => $"vai para a etapa '{transition.StageId}'",
            ClearStateAction => "limpa os valores",
            FinishSessionAction => "finaliza a sessão",
            CancelSessionAction => "cancela a sessão",
            _ => action.GetType().Name,
        });
        return $"{Trigger.Id}: {string.Join(", depois ", parts)}";
    }
}

/// <summary>One "read this field" checkbox of a button; its AutomationId is stable per field.</summary>
internal sealed class CaptureFieldChoice(string id, string label, bool isChecked)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public string AutomationId => "CaptureField_" + Id;

    public bool IsChecked { get; set; } = isChecked;
}
