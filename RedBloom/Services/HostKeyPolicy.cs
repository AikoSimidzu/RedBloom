using System.Windows;
using RedBloom.Terminal;
using RedBloom.Views;

namespace RedBloom.Services;

/// <summary>
/// Decides whether a server's host key may be trusted: matches it against the known-hosts
/// record and, when that is not conclusive, asks the user.
/// </summary>
public sealed class HostKeyPolicy
{
    private readonly KnownHostsStore _store;
    private readonly Window _owner;

    public HostKeyPolicy(KnownHostsStore store, Window owner)
    {
        _store = store;
        _owner = owner;
    }

    /// <summary>
    /// Runs on the SSH handshake thread and must return promptly, so it only consults the
    /// stored record and never shows anything.
    /// </summary>
    public bool IsTrusted(SshHostKey key) => _store.Check(key).Status == HostKeyStatus.Trusted;

    /// <summary>
    /// Asks the user about a key <see cref="IsTrusted"/> turned down. Called with no
    /// handshake in flight, so the user may take as long as verifying the fingerprint needs.
    /// </summary>
    public async Task<bool> ApproveAsync(SshHostKey key)
    {
        var (status, stored) = _store.Check(key);

        // The record can have changed since the handshake — re-check rather than assume.
        if (status == HostKeyStatus.Trusted)
        {
            return true;
        }

        var decision = await _owner.Dispatcher.InvokeAsync(
            () => HostKeyPrompt.Ask(_owner, key, status, stored));

        if (decision == HostKeyDecision.AcceptAndStore)
        {
            _store.Remember(key);
        }

        return decision != HostKeyDecision.Reject;
    }
}
