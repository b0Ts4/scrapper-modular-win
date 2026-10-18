using System.IO;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Finds the file a program has just opened (plan 16): the newest file matching
/// <see cref="FileSource.Pattern"/> in <see cref="FileSource.Folder"/> (environment variables
/// expanded) or one of its direct subfolders - a remote session's temp folder is
/// <c>Temp\&lt;session&gt;</c> -, written no longer than <see cref="FileSource.MaxAgeMinutes"/>
/// ago. Only names and times are looked at here; the content is read by the capture.
/// </summary>
public static class RecentFileFinder
{
    public static string? Find(FileSource source, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(source);
        var folder = Environment.ExpandEnvironmentVariables(source.Folder);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var oldest = now.AddMinutes(-source.MaxAgeMinutes);
        IEnumerable<string> folders = [folder];
        try
        {
            folders = folders.Concat(Directory.EnumerateDirectories(folder));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only the folder itself can be searched.
        }

        return folders
            .SelectMany(directory => Files(directory, source.Pattern))
            .Select(path => (Path: path, Written: File.GetLastWriteTime(path)))
            .Where(file => file.Written >= oldest && file.Written <= now.AddMinutes(1))
            .OrderByDescending(file => file.Written)
            .Select(file => file.Path)
            .FirstOrDefault();
    }

    private static IEnumerable<string> Files(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
