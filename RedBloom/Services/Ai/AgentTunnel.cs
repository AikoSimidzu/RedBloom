using System.Collections.Concurrent;
using RedBloom.Models;
using RedBloom.Terminal;

namespace RedBloom.Services.Ai;

/// <summary>
/// Reaches an endpoint that only listens on a remote machine's own loopback, by carrying the
/// connection over a saved SSH session.
/// </summary>
/// <remarks>
/// A model server put on a box is usually left unreachable from the network — the port is behind
/// a firewall, or it is bound to loopback on purpose. The usual answers are to open a port, which
/// is a decision about the machine's exposure, or to keep an ssh command running in a window,
/// which is a chore. Neither is necessary here: the app already speaks SSH and already forwards
/// ports, so the agent simply names a connection and the tunnel is raised when a chat needs it.
/// <para>
/// The forward is worked out from the agent's own address, so nothing has to be set up twice: an
/// agent pointed at <c>127.0.0.1:8080</c> gets exactly that port carried to the far side.
/// </para>
/// </remarks>
public static class AgentTunnel
{
    /// <summary>One connection per session, shared by every chat that asks for it.</summary>
    private static readonly ConcurrentDictionary<Guid, SshConnection> Open = new();

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// What the connection has to say about itself — a refused tunnel, a lost link.
    /// </summary>
    /// <remarks>
    /// Surfaced because these are the failures that otherwise look like the model being broken:
    /// the chat would report a connection refused on localhost with nothing to explain why the
    /// port that was supposed to be carried is not there.
    /// </remarks>
    public static event Action<string>? Notice;

    /// <summary>Decides whether a host key is already trusted, and asks when it is not.</summary>
    public static Func<SshHostKey, bool>? IsTrusted { get; set; }

    public static Func<SshHostKey, Task<bool>>? ApproveAsync { get; set; }

    /// <summary>True while a tunnel for this session is up.</summary>
    public static bool IsOpen(Guid session) =>
        Open.TryGetValue(session, out var live) && live.IsConnected;

    /// <summary>
    /// Raises the tunnel an agent needs, if it needs one. Null when it is ready, or the reason.
    /// </summary>
    public static async Task<string?> EnsureAsync(AiAgent agent, CancellationToken cancellationToken = default)
    {
        if (!agent.UsesTunnel || SessionCatalog.ById(agent.TunnelSessionId) is not { } session)
        {
            return agent.UsesTunnel ? LocalizationService.T("L_TunnelNoSession") : null;
        }

        if (IsOpen(session.Id))
        {
            return null;
        }

        if (Port(agent) is not { } port)
        {
            return LocalizationService.T("L_TunnelNoPort");
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsOpen(session.Id))
            {
                return null;
            }

            // A copy, because the forward this needs belongs to the tunnel rather than to the
            // saved connection — the user should not have to add it by hand, and it should not
            // appear in their session afterwards.
            var routed = session.Clone();

            if (!routed.Forwards.Any(f => f.Kind == PortForwardKind.Local && f.BoundPort == port))
            {
                routed.Forwards.Add(new PortForward
                {
                    Kind = PortForwardKind.Local,
                    BoundHost = "127.0.0.1",
                    BoundPort = port,
                    DestinationHost = "127.0.0.1",
                    DestinationPort = port,
                });
            }

            var connection = await SshConnection.EstablishAsync(
                routed,
                session.Secret,
                IsTrusted ?? (_ => false),
                ApproveAsync ?? (_ => Task.FromResult(false)),
                cancellationToken).ConfigureAwait(false);

            connection.Notice += line => Notice?.Invoke(line);
            connection.ConnectionLost += why => Notice?.Invoke(why);

            Open[session.Id] = connection;

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Format(LocalizationService.T("L_TunnelFailed"), ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Closes every tunnel. Called when the app is going away.</summary>
    public static void CloseAll()
    {
        foreach (var session in Open.Keys.ToList())
        {
            if (Open.TryRemove(session, out var connection))
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// The port to carry, taken from the agent's address.
    /// </summary>
    /// <remarks>
    /// Only a loopback address is tunnelled. An agent pointed at a real host is reachable as it
    /// stands, and quietly routing it through a machine would send the traffic somewhere the
    /// address does not say.
    /// </remarks>
    private static int? Port(AiAgent agent)
    {
        if (!Uri.TryCreate(agent.ResolvedBaseUrl, UriKind.Absolute, out var url))
        {
            return null;
        }

        return url.IsLoopback ? url.Port : null;
    }
}
