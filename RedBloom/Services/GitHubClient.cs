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
/// Two ways in. The browser sign-in (OAuth device flow) is the normal one: the user gets a short
/// code, authorises it on github.com, and never handles a token — it needs only a public client id
/// (see <see cref="ClientId"/>), no secret. Pasting a personal access token still works as a
/// fallback. Either way the resulting token is kept DPAPI-encrypted, never in plain text.
/// </remarks>
public static class GitHubClient
{
    private static readonly HttpClient Http = new();

    private static readonly string TokenFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "github.token");

    private static readonly string ClientIdFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "github.clientid");

    // The OAuth App's client id, used for the browser (device-flow) sign-in. It is public — not a
    // secret — so embedding it is fine; device flow never needs a client secret. A github.clientid
    // file beside the token overrides it, so the id can be changed without a rebuild.
    private const string EmbeddedClientId = "Ov23lioXxIJGCsTdwepE";

    /// <summary>The scopes the browser sign-in asks for: read the account, and read/write repositories.</summary>
    private const string Scopes = "repo read:user";

    /// <summary>The signed-in account's login, once connected.</summary>
    public static string Login { get; private set; } = string.Empty;

    /// <summary>Whether a token is stored and a login is known.</summary>
    public static bool IsConnected => Token().Length > 0;

    /// <summary>The OAuth App client id for the browser sign-in — from the override file, or embedded.</summary>
    public static string ClientId
    {
        get
        {
            try
            {
                if (File.Exists(ClientIdFile) && File.ReadAllText(ClientIdFile).Trim() is { Length: > 0 } fromFile)
                {
                    return fromFile;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall back to the embedded id.
            }

            return EmbeddedClientId;
        }
    }

    /// <summary>Whether the browser sign-in is available (an OAuth App client id is set).</summary>
    public static bool CanSignIn => ClientId.Length > 0;

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

    /// <summary>A started browser sign-in: the code to type, where to type it, and how to poll.</summary>
    public readonly record struct DeviceCode(string UserCode, string VerificationUri, string Device, int Interval, int ExpiresIn);

    /// <summary>
    /// Begins the browser sign-in (OAuth device flow): asks GitHub for a short code the user types
    /// into the verification page. Returns the code, or a message on failure.
    /// </summary>
    public static async Task<(DeviceCode? Code, string? Error)> StartDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSignIn)
        {
            return (null, "Browser sign-in is not configured yet (no OAuth App client id).");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("RedBloom");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scopes,
            });

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                return (null, root.TryGetProperty("error_description", out var d) ? d.GetString() : err.GetString());
            }

            var code = new DeviceCode(
                Str(root, "user_code"),
                Str(root, "verification_uri"),
                Str(root, "device_code"),
                root.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var i) ? i : 5,
                root.TryGetProperty("expires_in", out var ev) && ev.TryGetInt32(out var x) ? x : 900);

            return (code, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, $"Could not reach GitHub: {ex.Message}");
        }
    }

    /// <summary>
    /// Polls until the user finishes signing in in the browser (or it fails). On success the token
    /// is stored exactly as a pasted one would be. Returns null on success, or a message.
    /// </summary>
    public static async Task<string?> PollDeviceAsync(DeviceCode code, CancellationToken cancellationToken = default)
    {
        var interval = Math.Max(1, code.Interval);
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn > 0 ? code.ExpiresIn : 900);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd("RedBloom");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = code.Device,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                });

                using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Got it — hand the token to the same path a pasted one takes (verifies /user, stores it).
                if (root.TryGetProperty("access_token", out var at) && at.GetString() is { Length: > 0 } token)
                {
                    return await ConnectAsync(token, cancellationToken).ConfigureAwait(false);
                }

                switch (root.TryGetProperty("error", out var err) ? err.GetString() : null)
                {
                    case "authorization_pending":
                        break;                       // the user has not finished yet — keep waiting
                    case "slow_down":
                        interval += 5;               // GitHub asks us to poll less often
                        break;
                    case "expired_token":
                        return "The code expired. Start the sign-in again.";
                    case "access_denied":
                        return "Sign-in was cancelled on GitHub.";
                    case { Length: > 0 } other:
                        return other;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // A transient hiccup — keep polling until the deadline.
            }
        }

        return "Timed out waiting for the sign-in to finish.";
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
