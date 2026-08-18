using System.IO;
using System.Text.RegularExpressions;

namespace RedBloom.Services;

/// <summary>
/// Finds Visual Studio solutions on this machine, without the VS SDK: the default <c>source\repos</c>
/// tree and the paths Visual Studio remembers in its recent list. What is found can be linked to a
/// project as a source.
/// </summary>
public static partial class VisualStudioSources
{
    /// <summary>A discovered solution: its name, its <c>.sln</c> path, and the folder it lives in.</summary>
    public readonly record struct Solution(string Name, string Path, string Folder, DateTime Modified);

    /// <summary>Every solution found, newest first, with no duplicates.</summary>
    public static Task<List<Solution>> DiscoverAsync() => Task.Run(() =>
    {
        var found = new Dictionary<string, Solution>(StringComparer.OrdinalIgnoreCase);

        // The default place Visual Studio and "git clone" put repositories.
        var repos = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        ScanForSolutions(repos, found, depth: 4);

        // The paths Visual Studio remembers, dug out of its private settings file.
        foreach (var path in RecentFromSettings())
        {
            Add(found, path);
        }

        return found.Values.OrderByDescending(s => s.Modified).ToList();
    });

    private static void ScanForSolutions(string root, Dictionary<string, Solution> found, int depth)
    {
        if (depth < 0 || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.sln"))
            {
                Add(found, file);
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (name is ".git" or "bin" or "obj" or "node_modules" or "packages")
                {
                    continue;
                }

                ScanForSolutions(dir, found, depth - 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Skip a folder we cannot read.
        }
    }

    private static void Add(Dictionary<string, Solution> found, string slnPath)
    {
        try
        {
            if (!File.Exists(slnPath) || found.ContainsKey(slnPath))
            {
                return;
            }

            var folder = System.IO.Path.GetDirectoryName(slnPath) ?? string.Empty;
            found[slnPath] = new Solution(
                System.IO.Path.GetFileNameWithoutExtension(slnPath),
                slnPath,
                folder,
                File.GetLastWriteTime(slnPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A path we cannot inspect is simply not added.
        }
    }

    /// <summary>Solution paths Visual Studio has recorded, scraped from its private settings files.</summary>
    private static IEnumerable<string> RecentFromSettings()
    {
        var root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio");

        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var version in SafeDirs(root))
        {
            var settings = System.IO.Path.Combine(version, "ApplicationPrivateSettings.xml");

            string text;
            try
            {
                if (!File.Exists(settings))
                {
                    continue;
                }

                text = File.ReadAllText(settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (Match m in SolutionPathRegex().Matches(text))
            {
                var path = m.Value.Replace(@"\\", @"\");
                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^""<>|*?\r\n]+?)\.sln", RegexOptions.IgnoreCase)]
    private static partial Regex SolutionPathRegex();
}
