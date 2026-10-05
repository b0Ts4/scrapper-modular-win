using Prescriva.Agent.Application.Diagnostics;
using Prescriva.Agent.Infrastructure.Diagnostics;

namespace Prescriva.Agent.Infrastructure.Tests.Diagnostics;

public sealed class StructuredTechnicalLogTests
{
    [Fact]
    public void Log_writes_one_json_line_per_entry_with_codes_ids_and_timings()
    {
        using var writer = new StringWriter();
        using var log = new StructuredTechnicalLog(writer);
        var sessionId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        log.Log(new TechnicalLogEntry(
            TechnicalLogLevel.Debug,
            "field_capture_attempted",
            "Field 'name' capture outcome: Captured.",
            timestamp,
            SessionId: sessionId,
            TriggerId: "add",
            FieldId: "name",
            Provider: "uia",
            Confidence: 0.97,
            Elapsed: TimeSpan.FromMilliseconds(12)));

        var line = writer.ToString().Trim();
        Assert.Contains(sessionId.ToString(), line);
        Assert.Contains("field_capture_attempted", line);
        Assert.Contains("add", line);
        Assert.Contains("name", line);
        Assert.Contains("uia", line);
        Assert.Contains("0.97", line);
        Assert.Contains("12", line);
    }

    [Fact]
    public void Log_writes_one_line_per_call_so_multiple_entries_do_not_run_together()
    {
        using var writer = new StringWriter();
        using var log = new StructuredTechnicalLog(writer);
        var timestamp = DateTimeOffset.UtcNow;

        log.Log(new TechnicalLogEntry(TechnicalLogLevel.Info, "first", "first message", timestamp));
        log.Log(new TechnicalLogEntry(TechnicalLogLevel.Info, "second", "second message", timestamp));

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("first", lines[0]);
        Assert.Contains("second", lines[1]);
    }

    [Fact]
    public void Constructor_with_a_file_path_creates_the_directory_and_appends_across_instances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prescriva-technical-log-tests-" + Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "technical.log");
        try
        {
            using (var log = new StructuredTechnicalLog(filePath))
            {
                log.Log(new TechnicalLogEntry(TechnicalLogLevel.Info, "first", "first message", DateTimeOffset.UtcNow));
            }

            using (var log = new StructuredTechnicalLog(filePath))
            {
                log.Log(new TechnicalLogEntry(TechnicalLogLevel.Info, "second", "second message", DateTimeOffset.UtcNow));
            }

            var lines = File.ReadAllLines(filePath);
            Assert.Equal(2, lines.Length);
            Assert.Contains("first", lines[0]);
            Assert.Contains("second", lines[1]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Clear_empties_the_log_file_and_later_entries_are_still_written()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prescriva-technical-log-tests-" + Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "technical.log");
        try
        {
            using (var log = new StructuredTechnicalLog(filePath))
            {
                log.Log(new TechnicalLogEntry(TechnicalLogLevel.Info, "before", "before clear", DateTimeOffset.UtcNow));
                log.Log(new TechnicalLogEntry(TechnicalLogLevel.Info, "before", "before clear", DateTimeOffset.UtcNow));

                log.Clear();
                Assert.Equal(0, new FileInfo(filePath).Length);

                log.Log(new TechnicalLogEntry(TechnicalLogLevel.Info, "after", "after clear", DateTimeOffset.UtcNow));
            }

            var lines = File.ReadAllLines(filePath);
            var line = Assert.Single(lines);
            Assert.Contains("after", line);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
