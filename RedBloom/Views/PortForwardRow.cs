using System.ComponentModel;
using System.Runtime.CompilerServices;
using RedBloom.Models;

namespace RedBloom.Views;

/// <summary>
/// Editable view of a <see cref="PortForward"/>. Ports are held as text while the user types,
/// so a half-typed number does not have to round-trip through an int.
/// </summary>
public sealed class PortForwardRow : INotifyPropertyChanged
{
    private int _kindIndex;
    private string _boundHost;
    private string _boundPortText;
    private string _destinationHost;
    private string _destinationPortText;

    public PortForwardRow(PortForward? source = null)
    {
        source ??= new PortForward();

        _kindIndex = source.Kind switch
        {
            PortForwardKind.Remote => 1,
            PortForwardKind.Dynamic => 2,
            _ => 0,
        };
        _boundHost = source.BoundHost;
        _boundPortText = source.BoundPort > 0 ? source.BoundPort.ToString() : string.Empty;
        _destinationHost = source.DestinationHost;
        _destinationPortText = source.DestinationPort > 0 ? source.DestinationPort.ToString() : string.Empty;
    }

    public int KindIndex
    {
        get => _kindIndex;
        set
        {
            if (Set(ref _kindIndex, value))
            {
                OnPropertyChanged(nameof(NeedsDestination));
            }
        }
    }

    public string BoundPortText
    {
        get => _boundPortText;
        set => Set(ref _boundPortText, value);
    }

    public string DestinationHost
    {
        get => _destinationHost;
        set => Set(ref _destinationHost, value);
    }

    public string DestinationPortText
    {
        get => _destinationPortText;
        set => Set(ref _destinationPortText, value);
    }

    /// <summary>A SOCKS proxy has no fixed destination, so those fields are hidden for -D.</summary>
    public bool NeedsDestination => _kindIndex != 2;

    public bool TryBuild(out PortForward forward, out string? error)
    {
        forward = new PortForward();
        error = null;

        var kind = _kindIndex switch
        {
            1 => PortForwardKind.Remote,
            2 => PortForwardKind.Dynamic,
            _ => PortForwardKind.Local,
        };

        if (!int.TryParse(_boundPortText.Trim(), out var boundPort) || boundPort is < 1 or > 65535)
        {
            error = $"\"{_boundPortText}\" is not a valid listening port.";
            return false;
        }

        forward.Kind = kind;
        forward.BoundHost = string.IsNullOrWhiteSpace(_boundHost) ? "127.0.0.1" : _boundHost;
        forward.BoundPort = boundPort;

        if (kind == PortForwardKind.Dynamic)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_destinationHost))
        {
            error = "A tunnel needs a destination host.";
            return false;
        }

        if (!int.TryParse(_destinationPortText.Trim(), out var destinationPort)
            || destinationPort is < 1 or > 65535)
        {
            error = $"\"{_destinationPortText}\" is not a valid destination port.";
            return false;
        }

        forward.DestinationHost = _destinationHost.Trim();
        forward.DestinationPort = destinationPort;
        return true;
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
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
