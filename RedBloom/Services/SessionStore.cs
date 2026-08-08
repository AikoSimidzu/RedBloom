using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>Loads and persists the saved SSH sessions shown in the sidebar.</summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },

        // Keep host names and DPAPI blobs readable rather than \uXXXX-escaped; this file is
        // local configuration, never HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;

    public SessionStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RedBloom",
            "sessions.json");
    }

    public ObservableCollection<SshSession> Sessions { get; } = [];

    public void Load()
    {
        Sessions.Clear();

        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<SshSession>>(json, SerializerOptions);
            foreach (var session in loaded ?? [])
            {
                Sessions.Add(session);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not read {_filePath}: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(Sessions, SerializerOptions);

            // Write-then-replace so a crash mid-save cannot leave a truncated session list.
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not write {_filePath}: {ex.Message}");
        }
    }
}
