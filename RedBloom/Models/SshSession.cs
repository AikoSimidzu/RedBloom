using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using RedBloom.Services;

namespace RedBloom.Models;

public enum SshAuthKind
{
    Password,
    PrivateKey,
}

/// <summary>A saved SSH connection shown in the sidebar.</summary>
public sealed class SshSession : INotifyPropertyChanged
{
    private string _name = "New session";
    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private SshAuthKind _authKind = SshAuthKind.Password;
    private string? _privateKeyPath;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set => Set(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => Set(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    public SshAuthKind AuthKind
    {
        get => _authKind;
        set
        {
            if (Set(ref _authKind, value))
            {
                OnPropertyChanged(nameof(UsesPassword));
                OnPropertyChanged(nameof(UsesPrivateKey));
            }
        }
    }

    public string? PrivateKeyPath
    {
        get => _privateKeyPath;
        set => Set(ref _privateKeyPath, value);
    }

    /// <summary>DPAPI blob for the password, or for the key passphrase when using a key file.</summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>Tunnels opened alongside the shell, equivalent to ssh's -L / -R / -D.</summary>
    public List<PortForward> Forwards { get; set; } = [];

    /// <summary>
    /// Reopen the connection by itself when it drops — an idle timeout on the server side,
    /// a sleeping laptop, a Wi-Fi hiccup.
    /// </summary>
    public bool AutoReconnect { get; set; }

    [JsonIgnore]
    public bool UsesPassword => AuthKind == SshAuthKind.Password;

    [JsonIgnore]
    public bool UsesPrivateKey => AuthKind == SshAuthKind.PrivateKey;

    /// <summary>Short note for the sidebar, e.g. "2 tunnels". Empty when there are none.</summary>
    [JsonIgnore]
    public string ForwardSummary => Forwards.Count switch
    {
        0 => string.Empty,
        1 => Forwards[0].Display,
        var n => $"{n} tunnels",
    };

    [JsonIgnore]
    public bool HasForwards => Forwards.Count > 0;

    [JsonIgnore]
    public string DisplayTarget =>
        string.IsNullOrWhiteSpace(Username) ? Host : $"{Username}@{Host}" + (Port == 22 ? "" : $":{Port}");

    [JsonIgnore]
    public string? Secret
    {
        get => Secrets.Unprotect(ProtectedSecret);
        set => ProtectedSecret = Secrets.Protect(value);
    }

    public SshSession Clone() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Port = Port,
        Username = Username,
        AuthKind = AuthKind,
        PrivateKeyPath = PrivateKeyPath,
        ProtectedSecret = ProtectedSecret,
        Forwards = Forwards.Select(f => f.Clone()).ToList(),
        AutoReconnect = AutoReconnect,
    };

    public void CopyFrom(SshSession other)
    {
        Name = other.Name;
        Host = other.Host;
        Port = other.Port;
        Username = other.Username;
        AuthKind = other.AuthKind;
        PrivateKeyPath = other.PrivateKeyPath;
        ProtectedSecret = other.ProtectedSecret;
        Forwards = other.Forwards.Select(f => f.Clone()).ToList();
        AutoReconnect = other.AutoReconnect;
        OnPropertyChanged(nameof(DisplayTarget));
        OnPropertyChanged(nameof(ForwardSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(DisplayTarget));
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

