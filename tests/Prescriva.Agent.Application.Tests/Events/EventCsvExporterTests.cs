using System.Collections.Immutable;
using System.Globalization;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Domain.Events;

namespace Prescriva.Agent.Application.Tests.Events;

public sealed class EventCsvExporterTests
{
    private static readonly Guid Session = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Writes_a_header_and_one_row_per_event_with_field_columns_in_first_appearance_order()
    {
        var csv = Export(
            Event(1, "item_added", ("medicamento", "Dipirona"), ("quantidade", "2")),
            Event(2, "item_added", ("quantidade", "1"), ("lote", "A7")),
            Event(3, "budget_finished", items: 2));

        var lines = csv.Split("\r\n");
        Assert.Equal("sequencia;data_hora;evento;integracao;sessao;itens;medicamento;quantidade;lote", lines[0]);
        Assert.Equal($"1;2026-10-08 14:30:00;item_added;farmacia;{Session};0;Dipirona;2;", lines[1]);
        Assert.Equal($"2;2026-10-08 14:30:00;item_added;farmacia;{Session};0;;1;A7", lines[2]);
        Assert.Equal($"3;2026-10-08 14:30:00;budget_finished;farmacia;{Session};2;;;", lines[3]);
        Assert.Equal("", lines[4]); // the file ends with a line break
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public void Quotes_values_holding_the_separator_quotes_or_line_breaks()
    {
        var csv = Export(Event(1, "item_added", ("obs", "tomar 1x; em jejum"), ("nome", "Dipirona \"gotas\""), ("nota", "linha 1\nlinha 2")));

        // An event's own fields go alphabetically: nome, nota, obs.
        Assert.Contains(";\"Dipirona \"\"gotas\"\"\";\"linha 1\nlinha 2\";\"tomar 1x; em jejum\"\r\n", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=SOMA(A1:A9)", "'=SOMA(A1:A9)")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(1)", "'@SUM(1)")]
    [InlineData("\tvalor", "'\tvalor")]
    [InlineData("-5", "-5")]
    [InlineData("+3,5", "+3,5")]
    [InlineData("-1.234,56", "-1.234,56")]
    [InlineData("Dipirona", "Dipirona")]
    public void Neutralizes_values_a_spreadsheet_would_run_as_formulas_but_keeps_plain_numbers(string value, string expected)
    {
        var csv = Export(Event(1, "item_added", ("campo", value)));

        Assert.EndsWith(";" + expected + "\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Shows_a_file_field_by_its_description_never_by_its_internal_reference()
    {
        var reference = "attachment:" + new string('a', 64);
        var csv = Export(
            [Event(1, "item_added", ("receita", reference))],
            value => value == reference ? "receita.pdf" : null);

        Assert.Contains(";receita.pdf\r\n", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("attachment:", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_only_the_header_when_there_are_no_events()
    {
        Assert.Equal("sequencia;data_hora;evento;integracao;sessao;itens\r\n", Export());
    }

    private static string Export(params DomainEvent[] events) => Export(events, _ => null);

    private static string Export(DomainEvent[] events, Func<string, string?> describeAttachment)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        EventCsvExporter.Write(writer, events, describeAttachment, TimeZoneInfo.Utc);
        return writer.ToString();
    }

    private static DomainEvent Event(long sequence, string type, params (string Field, string Value)[] fields) =>
        Event(sequence, type, 0, fields);

    private static DomainEvent Event(long sequence, string type, int items, params (string Field, string Value)[] fields) =>
        new(
            Guid.NewGuid(),
            "farmacia",
            1,
            Session,
            sequence,
            new DateTimeOffset(2026, 10, 8, 14, 30, 0, TimeSpan.Zero),
            type,
            new DomainEventPayload(
                fields.ToImmutableDictionary(pair => pair.Field, pair => pair.Value),
                Enumerable.Range(0, items).Select(_ => ImmutableDictionary<string, string>.Empty).ToImmutableArray()));
}
