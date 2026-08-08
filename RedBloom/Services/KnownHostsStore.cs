using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RedBloom.Terminal;

namespace RedBloom.Services;

/// <summary>One remembered server key, keyed by host, port and key algorithm.</summary>
public sealed class KnownHost
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Algorithm { get; set; } = string.Empty;
    public string Sha256Fingerprint { get; set; } = string.Empty;
    public int KeyLength { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
}

/// <summary>
/// RedBloom's equivalent of <c>~/.ssh/known_hosts</c>. Kept separate from the session list
/// so deleting a saved session never silently discards the key that protects it.
/// </summary>
public sealed class KnownHostsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,

        // Base64 fingerprints contain '+', which the default encoder escapes to +. This
        // file exists to be compared against ssh-keygen output by eye, so it has to read the
        // same way. "Unsafe" here only means unsuitable for embedding in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly Lock _gate = new();
    private List<KnownHost> _hosts = [];
    private bool _loaded;
    private DateTime _loadedStamp;
    private long _loadedLength = -1;

    public KnownHostsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RedBloom",
            "known_hosts.json");
    }

    /// <summary>Compares a presented key against the record, without modifying anything.</summary>
    public (HostKeyStatus Status, KnownHost? Stored) Check(SshHostKey key)
    {
        lock (_gate)
        {
            EnsureLoaded();

            var forAlgorithm = Find(key.Host, key.Port, key.Algorithm);
            if (forAlgorithm is not null)
            {
                return string.Equals(forAlgorithm.Sha256Fingerprint, Normalize(key.Sha256Fingerprint), StringComparison.Ordinal)
                    ? (HostKeyStatus.Trusted, forAlgorithm)
                    : (HostKeyStatus.Changed, forAlgorithm);
            }

            var anyForHost = _hosts.Any(h =>
                string.Equals(h.Host, key.Host, StringComparison.OrdinalIgnoreCase) && h.Port == key.Port);

            return anyForHost ? (HostKeyStatus.NewAlgorithm, null) : (HostKeyStatus.Unknown, null);
        }
    }

    /// <summary>Records the key, replacing any previous entry for the same algorithm.</summary>
    public void Remember(SshHostKey key)
    {
        lock (_gate)
        {
            EnsureLoaded();

            var existing = Find(key.Host, key.Port, key.Algorithm);
            if (existing is not null)
            {
                _hosts.Remove(existing);
            }

            _hosts.Add(new KnownHost
            {
                Host = key.Host,
                Port = key.Port,
                Algorithm = key.Algorithm,
                Sha256Fingerprint = Normalize(key.Sha256Fingerprint),
                KeyLength = key.KeyLength,
                FirstSeen = DateTimeOffset.Now,
            });

            Save();
        }
    }

    private KnownHost? Find(string host, int port, string algorithm) =>
        _hosts.FirstOrDefault(h =>
            string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase)
            && h.Port == port
            && string.Equals(h.Algorithm, algorithm, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stores fingerprints bare, so a stored "SHA256:x" still matches a presented "x".</summary>
    private static string Normalize(string fingerprint) =>
        fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? fingerprint["SHA256:".Length..]
            : fingerprint;

    /// <summary>
    /// Loads the file, and reloads it whenever it has changed on disk. Holding a stale copy
    /// would mean a key the user revoked by hand — or from another RedBloom window — kept
    /// being trusted for the rest of the session.
    /// </summary>
    private void EnsureLoaded()
    {
        var info = new FileInfo(_filePath);
        var exists = info.Exists;
        var stamp = exists ? info.LastWriteTimeUtc : default;
        var length = exists ? info.Length : -1;

        if (_loaded && stamp == _loadedStamp && length == _loadedLength)
        {
            return;
        }

        _loaded = true;
        _loadedStamp = stamp;
        _loadedLength = length;

        if (!exists)
        {
            _hosts = [];
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _hosts = JsonSerializer.Deserialize<List<KnownHost>>(json, SerializerOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable known-hosts file must not be treated as "everything is trusted":
            // an empty list means every host looks new and gets challenged.
            Debug.WriteLine($"Could not read {_filePath}: {ex.Message}");
            _hosts = [];
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_hosts, SerializerOptions);
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _filePath, overwrite: true);

            // Record what we just wrote so the next read does not count it as an outside edit.
            var info = new FileInfo(_filePath);
            _loadedStamp = info.LastWriteTimeUtc;
            _loadedLength = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not write {_filePath}: {ex.Message}");
        }
    }
}
