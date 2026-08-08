using System.Security.Cryptography;
using System.Text;

namespace RedBloom.Services;

/// <summary>
/// Wraps secrets with Windows DPAPI under the current user account, so the on-disk
/// session file is useless on any other machine or to any other user.
/// </summary>
public static class Secrets
{
    // Ties the ciphertext to this app; a blob lifted into another DPAPI consumer won't open.
    private static readonly byte[] Entropy = "RedBloom.SessionSecret.v1"u8.ToArray();

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(bytes);
        return Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            var encrypted = Convert.FromBase64String(ciphertext);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var plaintext = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return plaintext;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Copied from another machine or user profile: the secret is simply unavailable.
            return null;
        }
    }
}
