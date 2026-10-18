using System.IO;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.Tests.Automation;

/// <summary>
/// The file a program has just opened (plan 16): FórmulaCerta's MobyPharma writes every opened
/// attachment as <c>anexo_YYYYMMDD_HHMMSSmmm.ext</c> in the session's temp folder.
/// </summary>
public sealed class RecentFileFinderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 18, 18, 40, 0, DateTimeKind.Local);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prescriva-recent-" + Guid.NewGuid().ToString("N"));

    public RecentFileFinderTests() => Directory.CreateDirectory(Path.Combine(_root, "16"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_newest_matching_file_in_the_folder_or_a_direct_subfolder_is_chosen()
    {
        Write("16\\anexo_20261018_183315390.pdf", Now.AddMinutes(-7));
        Write("16\\anexo_20261018_183435722.pdf", Now.AddMinutes(-5));
        Write("anexo_20261018_183000000.jpg", Now.AddMinutes(-10));
        Write("16\\outro_arquivo.pdf", Now.AddMinutes(-1));

        var found = RecentFileFinder.Find(new FileSource(_root, "anexo_*", 60), Now);

        Assert.Equal(Path.Combine(_root, "16", "anexo_20261018_183435722.pdf"), found);
    }

    [Fact]
    public void A_file_older_than_the_maximum_age_is_not_taken()
    {
        Write("16\\anexo_antigo.pdf", Now.AddMinutes(-61));

        Assert.Null(RecentFileFinder.Find(new FileSource(_root, "anexo_*", 60), Now));
    }

    [Fact]
    public void Environment_variables_in_the_folder_are_expanded_and_a_missing_folder_finds_nothing()
    {
        Write("anexo_1.png", Now.AddMinutes(-1));
        Environment.SetEnvironmentVariable("PRESCRIVA_TEST_ROOT", _root);

        Assert.Equal(Path.Combine(_root, "anexo_1.png"), RecentFileFinder.Find(new FileSource("%PRESCRIVA_TEST_ROOT%", "anexo_*", 60), Now));
        Assert.Null(RecentFileFinder.Find(new FileSource(Path.Combine(_root, "nao_existe"), "anexo_*", 60), Now));
    }

    private void Write(string relative, DateTime lastWrite)
    {
        var path = Path.Combine(_root, relative);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTime(path, lastWrite);
    }
}
