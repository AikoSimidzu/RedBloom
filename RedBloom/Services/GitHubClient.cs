using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RedBloom.Services;

/// <summary>
/// Talks to GitHub on the user's behalf with a personal access token: signs in, remembers who, and
/// lists the repositories the account can see. The token is kept DPAPI-encrypted under the user's
/// profile, never in plain text.
/// </summary>
/// <remarks>
/// A personal access token rather than an OAuth sign-in flow: RedBloom is not a registered GitHub
/// OAuth app, so there is no client id to run device flow with. A fine-grained token the user makes
/// (with read access to repositories) is the direct, dependency-free path.
/// </remarks>
public static class GitHubClient
{
    private static readonly HttpClient Http = new();

    private static readonly string TokenFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "github.token");

    /// <summary>The signed-in account's login, once connected.</summary>
    public static string Login { get; private set; } = string.Empty;

    /// <summary>Whether a token is stored and a login is known.</summary>
    public static bool IsConnected => Token().Length > 0;

    /// <summary>A repository the account can see.</summary>
    public readonly record struct Repo(string FullName, string Name, string Owner, string Url, string CloneUrl, bool Private, string Description, int Stars);

    /// <summary>
    /// Signs in with a token: checks it against <c>/user</c>, and on success remembers it and the
    /// login. Returns null on success, or a message to show on failure.
    /// </summary>
    public static async Task<string?> ConnectAsync(string token, CancellationToken cancellationToken = default)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            return "Enter a token.";
        }

        try
        {
            using var request = Build(HttpMethod.Get, "https://api.github.com/user", token);
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "The token was rejected (401). Check it has repository read access."
                    : $"GitHub returned {(int)response.StatusCode}.";
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            Login = doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() ?? string.Empty : string.Empty;

            Save(token);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"Could not reach GitHub: {ex.Message}";
        }
    }

    /// <summary>Forgets the stored token.</summary>
    public static void Disconnect()
    {
        Login = string.Empty;

        try
        {
            if (File.Exists(TokenFile))
            {
                File.Delete(TokenFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not clear GitHub token: {ex.Message}");
        }
    }

    /// <summary>The repositories the account owns or collaborates on, newest first.</summary>
    public static async Task<List<Repo>> ListReposAsync(CancellationToken cancellationToken = default)
    {
        var token = Token();
        if (token.Length == 0)
        {
            return [];
        }

        var repos = new List<Repo>();

        try
        {
            for (var page = 1; page <= 5; page++)
            {
                var url = $"https://api.github.com/user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member&page={page}";
                using var request = Build(HttpMethod.Get, url, token);
                using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    break;
                }

                foreach (var r in doc.RootElement.EnumerateArray())
                {
                    repos.Add(new Repo(
                        Str(r, "full_name"),
                        Str(r, "name"),
                        r.TryGetProperty("owner", out var owner) ? Str(owner, "login") : string.Empty,
                        Str(r, "html_url"),
                        Str(r, "clone_url"),
                        r.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
                        Str(r, "description"),
                        r.TryGetProperty("stargazers_count", out var s) && s.TryGetInt32(out var stars) ? stars : 0));
                }

                if (doc.RootElement.GetArrayLength() < 100)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not list GitHub repos: {ex.Message}");
        }

        return repos;
    }

    /// <summary>The stored token, for git operations (clone/push) in this process. Empty when signed out.</summary>
    internal static string CurrentToken() => Token();

    /// <summary>
    /// Creates a repository on the account. Returns its full name and clone URL, or null with a
    /// message on failure.
    /// </summary>
    public static async Task<(string FullName, string CloneUrl, string HtmlUrl)?> CreateRepoAsync(
        string name, bool priv, CancellationToken cancellationToken = default)
    {
        var token = Token();
        if (token.Length == 0)
        {
            return null;
        }

        try
        {
            using var request = Build(HttpMethod.Post, "https://api.github.com/user/repos", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { name, @private = priv, auto_init = false }),
                Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return (Str(root, "full_name"), Str(root, "clone_url"), Str(root, "html_url"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("RedBloom");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    // ---- token storage (DPAPI) ----

    private static void Save(string token)
    {
        try
        {
            var dir = Path.GetDirectoryName(TokenFile);
            if (dir is { Length: > 0 })
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(TokenFile, Protect(Encoding.UTF8.GetBytes(token)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save GitHub token: {ex.Message}");
        }
    }

    private static string Token()
    {
        try
        {
            return File.Exists(TokenFile) ? Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(TokenFile))) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return string.Empty;
        }
    }

    // Windows DPAPI, so the token at rest is bound to this user account.
    private static byte[] Protect(byte[] data) => Crypt(data, encrypt: true);
    private static byte[] Unprotect(byte[] data) => Crypt(data, encrypt: false);

    private sealed class CryptographicException(string message) : Exception(message);

    private static byte[] Crypt(byte[] data, bool encrypt)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);

        try
        {
            input.Size = data.Length;
            input.Data = handle.AddrOfPinnedObject();

            var ok = encrypt
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output);

            if (!ok)
            {
                throw new CryptographicException("DPAPI call failed.");
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            handle.Free();
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
