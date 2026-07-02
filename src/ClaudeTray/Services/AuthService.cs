using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeTray.Services;

/// <summary>
/// Sign-in paths: OAuth PKCE against the Claude Code public client, import of an existing
/// Claude Code .credentials.json (native or via \\wsl$), or a pasted token. Handles refresh.
/// </summary>
public sealed class AuthService
{
    // Claude Code's public OAuth client (PKCE — no secret involved).
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const int CallbackPort = 54545;
    private static readonly string RedirectUri = $"http://localhost:{CallbackPort}/callback";
    private const string Scopes = "user:profile user:inference";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public AuthService(HttpClient http) => _http = http;

    public OAuthTokens? Tokens { get; private set; }

    public bool IsSignedIn => Tokens is not null;

    public event Action? StateChanged;

    public void Initialize() => Tokens = TokenStore.Load();

    public void SignOut()
    {
        Tokens = null;
        TokenStore.Delete();
        StateChanged?.Invoke();
    }

    /// <summary>Returns a valid access token, refreshing if needed. Throws AuthException when sign-in is required.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var t = Tokens ?? throw new AuthException("Not signed in.");
        if (!t.IsExpired) return t.AccessToken;
        await RefreshAsync(ct);
        return Tokens?.AccessToken ?? throw new AuthException("Sign-in required.");
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var t = Tokens;
            if (t is null || !t.IsExpired) return; // another caller already refreshed
            if (string.IsNullOrEmpty(t.RefreshToken))
                throw new AuthException("Token expired and no refresh token is available. Sign in again.");

            var resp = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = t.RefreshToken!,
                ["client_id"] = ClientId,
            }, ct);

            SetTokens(resp, fallbackRefreshToken: t.RefreshToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Full browser sign-in. Blocks until the loopback redirect arrives or ct fires.</summary>
    public async Task SignInWithBrowserAsync(CancellationToken ct)
    {
        var verifier = RandomUrlSafe(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomUrlSafe(32);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/");
        listener.Start();

        var url = $"{AuthorizeUrl}?code=true&response_type=code&client_id={ClientId}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                  $"&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var ctxTask = listener.GetContextAsync();
        var done = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, ct));
        if (done != ctxTask) { ct.ThrowIfCancellationRequested(); }
        var ctx = await ctxTask;

        var query = ctx.Request.QueryString;
        var code = query["code"];
        var returnedState = query["state"];

        string html;
        if (string.IsNullOrEmpty(code) || returnedState != state)
        {
            html = "<html><body style='font-family:sans-serif'><h2>Sign-in failed</h2>You can close this tab.</body></html>";
            await WriteResponseAsync(ctx.Response, html);
            throw new AuthException(query["error"] is { } err ? $"Authorization failed: {err}" : "Authorization failed (missing code or state mismatch).");
        }

        html = "<html><body style='font-family:sans-serif'><h2>claude-tray is connected 🎉</h2>You can close this tab.</body></html>";
        await WriteResponseAsync(ctx.Response, html);

        var resp = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
            ["state"] = state,
        }, ct);

        SetTokens(resp, fallbackRefreshToken: null);
    }

    /// <summary>Import tokens from a Claude Code .credentials.json file.</summary>
    public void ImportCredentialsFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            throw new AuthException("File does not contain a claudeAiOauth section.");

        Tokens = new OAuthTokens
        {
            AccessToken = oauth.GetProperty("accessToken").GetString() ?? throw new AuthException("accessToken missing."),
            RefreshToken = oauth.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null,
            ExpiresAt = oauth.TryGetProperty("expiresAt", out var ea) ? ea.GetInt64() : 0,
        };
        TokenStore.Save(Tokens);
        StateChanged?.Invoke();
    }

    public void ImportPastedTokens(string accessToken, string? refreshToken)
    {
        Tokens = new OAuthTokens
        {
            AccessToken = accessToken.Trim(),
            RefreshToken = string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken.Trim(),
            // Unknown expiry — assume expired soon so a refresh token gets exercised early.
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        };
        TokenStore.Save(Tokens);
        StateChanged?.Invoke();
    }

    /// <summary>All plausible Claude Code credential files: native %USERPROFILE% plus every WSL distro home.</summary>
    public static List<string> DiscoverCredentialFiles()
    {
        var found = new List<string>();
        var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        if (File.Exists(native)) found.Add(native);

        foreach (var distro in ListWslDistros())
        {
            foreach (var root in new[] { $@"\\wsl$\{distro}\home", $@"\\wsl.localhost\{distro}\home" })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var home in Directory.GetDirectories(root))
                    {
                        var p = Path.Combine(home, ".claude", ".credentials.json");
                        if (File.Exists(p)) found.Add(p);
                    }
                    break; // one root scheme per distro is enough
                }
                catch { /* distro not running / access denied — skip */ }
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> ListWslDistros()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "-l -q")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode, // wsl.exe emits UTF-16
            };
            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<string>();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim().Trim('\0'))
                         .Where(l => l.Length > 0)
                         .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                SignOut();
                throw new AuthException($"Token request rejected ({(int)resp.StatusCode}). Sign in again.");
            }
            throw new HttpRequestException($"Token endpoint returned {(int)resp.StatusCode}: {body}");
        }
        return JsonSerializer.Deserialize<TokenResponse>(body)
               ?? throw new AuthException("Empty token response.");
    }

    private void SetTokens(TokenResponse resp, string? fallbackRefreshToken)
    {
        Tokens = new OAuthTokens
        {
            AccessToken = resp.AccessToken ?? throw new AuthException("No access_token in response."),
            RefreshToken = resp.RefreshToken ?? fallbackRefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(resp.ExpiresIn > 0 ? resp.ExpiresIn : 3600).ToUnixTimeMilliseconds(),
        };
        TokenStore.Save(Tokens);
        StateChanged?.Invoke();
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static string RandomUrlSafe(int bytes)
    {
        Span<byte> buf = stackalloc byte[64];
        RandomNumberGenerator.Fill(buf[..bytes]);
        return Base64Url(buf[..bytes].ToArray());
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
    }
}

public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}
