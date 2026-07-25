using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClaudeTray.Models;

namespace ClaudeTray.Services;

/// <summary>The usage endpoint rate-limited us. <see cref="RetryAfter"/> is the server's
/// hint, if it sent a usable one — it often sends "Retry-After: 0", which is no hint at all.</summary>
public sealed class RateLimitException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public RateLimitException(TimeSpan? retryAfter)
        : base("Rate limited by Anthropic.") => RetryAfter = retryAfter;
}

public sealed class UsageClient
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    private readonly HttpClient _http;
    private readonly AuthService _auth;

    public UsageClient(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        var body = await SendAsync(await _auth.GetAccessTokenAsync(ct), ct);
        if (body is null)
        {
            // 401 → one forced refresh, then retry once.
            await _auth.RefreshAsync(ct);
            body = await SendAsync(await _auth.GetAccessTokenAsync(ct), ct)
                   ?? throw new AuthException("Usage request unauthorized after token refresh. Sign in again.");
        }

        var parsed = JsonSerializer.Deserialize<UsageResponse>(body)
                     ?? throw new HttpRequestException("Empty usage response.");
        return new UsageSnapshot
        {
            Limits = parsed.Limits?.Where(l => l.Percent is not null || l.ResetsAt is not null).ToList()
                     ?? new List<LimitEntry>(),
            ExtraUsage = parsed.ExtraUsage,
        };
    }

    /// <summary>Returns the response body, or null on 401 (caller refreshes and retries).</summary>
    private async Task<string?> SendAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = resp.Headers.RetryAfter?.Delta
                             ?? (resp.Headers.RetryAfter?.Date is { } date
                                 ? date - DateTimeOffset.UtcNow
                                 : null);
            throw new RateLimitException(retryAfter > TimeSpan.Zero ? retryAfter : null);
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
