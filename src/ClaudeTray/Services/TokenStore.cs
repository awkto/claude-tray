using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeTray.Services;

public sealed class OAuthTokens
{
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    /// <summary>Unix milliseconds, matching Claude Code's .credentials.json convention.</summary>
    public long ExpiresAt { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ExpiresAt - 60_000;
}

/// <summary>Persists OAuth tokens DPAPI-encrypted (current user) under %AppData%\claude-tray.</summary>
public static class TokenStore
{
    private static string TokenPath => Path.Combine(SettingsService.ConfigDir, "tokens.bin");

    public static OAuthTokens? Load()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<OAuthTokens>(plain);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(OAuthTokens tokens)
    {
        Directory.CreateDirectory(SettingsService.ConfigDir);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens));
        File.WriteAllBytes(TokenPath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }

    public static void Delete()
    {
        try { File.Delete(TokenPath); } catch { /* nothing to clear */ }
    }
}
