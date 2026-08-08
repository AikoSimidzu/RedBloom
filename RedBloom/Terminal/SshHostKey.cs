namespace RedBloom.Terminal;

/// <summary>The host key a server presented during the handshake.</summary>
/// <param name="Sha256Fingerprint">
/// Base64 SHA-256 digest of the key, the same value OpenSSH prints after "SHA256:".
/// </param>
public sealed record SshHostKey(
    string Host,
    int Port,
    string Algorithm,
    string Sha256Fingerprint,
    int KeyLength)
{
    /// <summary>The fingerprint in the form users see elsewhere, e.g. in ssh-keygen output.</summary>
    public string DisplayFingerprint =>
        Sha256Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)
            ? Sha256Fingerprint
            : $"SHA256:{Sha256Fingerprint}";

    public string Endpoint => Port == 22 ? Host : $"{Host}:{Port}";

    /// <summary>Drops an optional "SHA256:" prefix so stored and presented forms compare equal.</summary>
    public static string NormalizeFingerprint(string fingerprint) =>
        fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? fingerprint["SHA256:".Length..]
            : fingerprint;
}

/// <summary>How a presented key compares to what we have on record.</summary>
public enum HostKeyStatus
{
    /// <summary>Nothing stored for this host and port; first contact.</summary>
    Unknown,

    /// <summary>Matches the stored fingerprint exactly.</summary>
    Trusted,

    /// <summary>
    /// A key of this algorithm is stored and the fingerprint does not match. This is the
    /// case that indicates interception.
    /// </summary>
    Changed,

    /// <summary>
    /// The host is known, but has never presented a key of this algorithm before. Less
    /// alarming than <see cref="Changed"/>, but not the plain first-contact case either.
    /// </summary>
    NewAlgorithm,
}

public enum HostKeyDecision
{
    /// <summary>Abort the connection.</summary>
    Reject,

    /// <summary>Proceed, but do not write anything to the known-hosts file.</summary>
    AcceptOnce,

    /// <summary>Proceed and remember this key for future connections.</summary>
    AcceptAndStore,
}
