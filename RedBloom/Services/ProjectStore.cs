using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// Every saved project, one metadata file per project, plus its real folder on disk.
/// </summary>
/// <remarks>
/// A sibling of <see cref="RoomStore"/>: the project's metadata lives as its own JSON file under
/// <c>%APPDATA%\RedBloom\projects</c>, while the work itself lives in a user-visible folder under
/// <see cref="ProjectsRoot"/> that RedBloom creates when the project is made.
/// </remarks>
public static class ProjectStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "projects");

    /// <summary>The user-visible root every project's own folder is created under.</summary>
    public static string ProjectsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RedBloom Projects");

    private static bool _loaded;

    /// <summary>All known projects, newest first.</summary>
    public static ObservableCollection<Project> Projects { get; } = [];

    public static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            var loaded = new List<Project>();

            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<Project>(File.ReadAllText(file), SerializerOptions)
                        is { } project && project.Id.Length > 0)
                    {
                        loaded.Add(project);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    Debug.WriteLine($"Skipping unreadable project {file}: {ex.Message}");
                }
            }

            foreach (var project in loaded.OrderByDescending(p => p.UpdatedAt))
            {
                Projects.Add(project);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not read the projects folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes a new project with its own folder on disk under <see cref="ProjectsRoot"/>, gives a
    /// name that is already taken a numbered sibling, and saves it.
    /// </summary>
    public static Project Create(string name, string description)
    {
        var project = new Project
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Project" : name.Trim(),
            Description = description.Trim(),
        };

        project.Folder = MakeFolder(project.Name);
        Save(project);
        return project;
    }

    private static string MakeFolder(string name)
    {
        var safe = Sanitize(name);

        try
        {
            var path = Path.Combine(ProjectsRoot, safe);

            for (var i = 2; Directory.Exists(path); i++)
            {
                path = Path.Combine(ProjectsRoot, $"{safe} ({i})");
            }

            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"Could not make a project folder for {name}: {ex.Message}");
            return Path.Combine(ProjectsRoot, safe);
        }
    }

    /// <summary>Writes a project, adding it to the list the first time it is saved.</summary>
    public static void Save(Project project)
    {
        if (!Projects.Contains(project))
        {
            Projects.Insert(0, project);
        }

        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, project.Id + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(project, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save project {project.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a project's metadata. The folder on disk is left alone — deleting a project must
    /// never quietly take the user's files with it.
    /// </summary>
    public static void Delete(Project project)
    {
        Projects.Remove(project);

        try
        {
            var path = Path.Combine(Folder, project.Id + ".json");

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not delete project {project.Id}: {ex.Message}");
        }
    }

    private static string Sanitize(string name)
    {
        var chars = name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray();
        var safe = new string(chars).Trim();
        return safe.Length > 0 ? safe : "Project";
    }
}
