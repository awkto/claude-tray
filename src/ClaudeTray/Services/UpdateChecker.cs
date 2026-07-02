using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ClaudeTray.Services;

/// <summary>Daily check of the GitHub Releases API; exposes the newer version's URL if any.</summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/awkto/claude-tray/releases/latest";

    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public string? AvailableVersion { get; private set; }
    public string? ReleaseUrl { get; private set; }

    public event Action? UpdateFound;

    public UpdateChecker(HttpClient http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";

    public async Task MaybeCheckAsync(CancellationToken ct)
    {
        if (!_settings.Current.CheckForUpdates) return;
        if (DateTimeOffset.Now - _lastCheck < TimeSpan.FromDays(1)) return;
        _lastCheck = DateTimeOffset.Now;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            req.Headers.TryAddWithoutValidation("User-Agent", "claude-tray");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = tag.TrimStart('v');
            if (Version.TryParse(latest, out var latestV) &&
                Version.TryParse(CurrentVersion.Split('-')[0], out var currentV) &&
                latestV > currentV)
            {
                AvailableVersion = latest;
                ReleaseUrl = doc.RootElement.GetProperty("html_url").GetString();
                UpdateFound?.Invoke();
            }
        }
        catch
        {
            // Update checks are best-effort.
        }
    }
}
