using System.Text.Json.Serialization;

namespace RedBloom.Models;

public enum PortForwardKind
{
    /// <summary>ssh -L: listen here, connect from the server.</summary>
    Local,

    /// <summary>ssh -R: listen on the server, connect from here.</summary>
    Remote,

    /// <summary>ssh -D: a SOCKS proxy listening here.</summary>
    Dynamic,
}

/// <summary>One tunnel carried by a session, mirroring ssh's -L / -R / -D.</summary>
public sealed class PortForward
{
    /// <summary>Interface the listening end binds to. Loopback keeps the tunnel private.</summary>
    public string BoundHost { get; set; } = "127.0.0.1";

    public int BoundPort { get; set; }

    /// <summary>Destination host, resolved by whichever side does the connecting. Unused for SOCKS.</summary>
    public string DestinationHost { get; set; } = "localhost";

    public int DestinationPort { get; set; }

    public PortForwardKind Kind { get; set; } = PortForwardKind.Local;

    [JsonIgnore]
    public string Display => Kind switch
    {
        PortForwardKind.Dynamic => $"SOCKS on {BoundHost}:{BoundPort}",
        PortForwardKind.Remote => $"remote {BoundHost}:{BoundPort} → {DestinationHost}:{DestinationPort}",
        _ => $"{BoundHost}:{BoundPort} → {DestinationHost}:{DestinationPort}",
    };

    /// <summary>The equivalent ssh flag, so a session can be reproduced on the command line.</summary>
    [JsonIgnore]
    public string SshFlag => Kind switch
    {
        PortForwardKind.Dynamic => $"-D {BoundHost}:{BoundPort}",
        PortForwardKind.Remote => $"-R {BoundHost}:{BoundPort}:{DestinationHost}:{DestinationPort}",
        _ => $"-L {BoundHost}:{BoundPort}:{DestinationHost}:{DestinationPort}",
    };

    public bool IsValid =>
        BoundPort is > 0 and <= 65535
        && (Kind == PortForwardKind.Dynamic
            || (DestinationPort is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(DestinationHost)));

    public PortForward Clone() => new()
    {
        BoundHost = BoundHost,
        BoundPort = BoundPort,
        DestinationHost = DestinationHost,
        DestinationPort = DestinationPort,
        Kind = Kind,
    };
}
