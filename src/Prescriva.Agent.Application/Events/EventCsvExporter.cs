using System.Globalization;
using System.Text.RegularExpressions;
using Prescriva.Agent.Domain.Events;

namespace Prescriva.Agent.Application.Events;

/// <summary>
/// Writes captured events as CSV that opens correctly in Excel (pt-BR) and other spreadsheets:
/// <c>;</c> separator, CRLF line ends, RFC 4180 quoting. One row per event; fixed columns
/// <c>sequencia;data_hora;evento;integracao;sessao;itens</c>, then one column per captured field
/// in the order fields first appear (an event's own fields alphabetically). <c>itens</c> is the
/// number of items the event's session had confirmed at that point (each item also has its own
/// <c>item_added</c> row).
///
/// A value a spreadsheet would run as a formula (starting with <c>=</c>, <c>+</c>, <c>-</c>,
/// <c>@</c>, tab or CR) is prefixed with <c>'</c> unless it is a plain number. A file field shows
/// its description, never its internal attachment reference.
/// </summary>
public static partial class EventCsvExporter
{
    private const char Separator = ';';
    private const string LineEnd = "\r\n";
    private static readonly string[] FixedColumns = ["sequencia", "data_hora", "evento", "integracao", "sessao", "itens"];

    /// <param name="describeAttachment">The description of an attachment reference (e.g. its file name), or null if unknown.</param>
    /// <param name="timeZone">The time zone timestamps are shown in (the operator's local one).</param>
    public static void Write(
        TextWriter writer,
        IReadOnlyList<DomainEvent> events,
        Func<string, string?> describeAttachment,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(describeAttachment);
        ArgumentNullException.ThrowIfNull(timeZone);

        var fieldColumns = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var domainEvent in events)
        {
            foreach (var field in domainEvent.Payload.Fields.Keys.Order(StringComparer.Ordinal))
            {
                if (known.Add(field))
                {
                    fieldColumns.Add(field);
                }
            }
        }

        WriteRow(writer, FixedColumns.Concat(fieldColumns));
        foreach (var domainEvent in events)
        {
            var fields = domainEvent.Payload.Fields;
            var items = domainEvent.Payload.Items.IsDefault ? 0 : domainEvent.Payload.Items.Length;
            WriteRow(writer, new[]
            {
                domainEvent.Sequence.ToString(CultureInfo.InvariantCulture),
                TimeZoneInfo.ConvertTime(domainEvent.Timestamp, timeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                domainEvent.Type,
                domainEvent.ConfigurationId,
                domainEvent.SessionId.ToString(),
                items.ToString(CultureInfo.InvariantCulture),
            }.Concat(fieldColumns.Select(field => fields.TryGetValue(field, out var value) ? Display(value, describeAttachment) : string.Empty)));
        }
    }

    private static string Display(string value, Func<string, string?> describeAttachment) =>
        Capture.AttachmentReference.IsReference(value)
            ? describeAttachment(value) ?? "arquivo (indisponível)"
            : value;

    private static void WriteRow(TextWriter writer, IEnumerable<string> cells)
    {
        writer.Write(string.Join(Separator, cells.Select(Cell)));
        writer.Write(LineEnd);
    }

    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !PlainNumber().IsMatch(value))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([Separator, '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    [GeneratedRegex(@"^[+-]?\d[\d.,]*$")]
    private static partial Regex PlainNumber();
}
