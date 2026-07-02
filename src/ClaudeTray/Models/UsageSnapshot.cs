using System.Text.Json.Serialization;

namespace ClaudeTray.Models;

/// <summary>One entry of the generic limits[] array from /api/oauth/usage.</summary>
public sealed class LimitEntry
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("percent")] public double? Percent { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("resets_at")] public DateTimeOffset? ResetsAt { get; set; }
    [JsonPropertyName("scope")] public LimitScope? Scope { get; set; }

    /// <summary>Stable identity for notification state — survives percent changes, unique per bucket.</summary>
    [JsonIgnore]
    public string Key => Scope?.Model?.DisplayName is { Length: > 0 } m ? $"{Kind}:{m}" : Kind;

    [JsonIgnore]
    public string Label => Kind switch
    {
        "session" => "Session",
        "weekly_all" => "Week · All",
        "weekly_scoped" when Scope?.Model?.DisplayName is { Length: > 0 } m => $"Week · {m}",
        _ => Scope?.Model?.DisplayName is { Length: > 0 } m2 ? $"{Capitalize(Group)} · {m2}" : Capitalize(Kind.Replace('_', ' ')),
    };

    /// <summary>Short label for the flyout bar column.</summary>
    [JsonIgnore]
    public string ShortLabel => Kind switch
    {
        "session" => "Session",
        "weekly_all" => "Week",
        _ => Scope?.Model?.DisplayName is { Length: > 0 } m ? m : Capitalize(Kind.Replace('_', ' ')),
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

public sealed class LimitScope
{
    [JsonPropertyName("model")] public LimitScopeModel? Model { get; set; }
    [JsonPropertyName("surface")] public LimitScopeSurface? Surface { get; set; }
}

public sealed class LimitScopeModel
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
}

public sealed class LimitScopeSurface
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
}

public sealed class ExtraUsage
{
    [JsonPropertyName("is_enabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("monthly_limit")] public double? MonthlyLimit { get; set; }
    [JsonPropertyName("used_credits")] public double? UsedCredits { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("decimal_places")] public int? DecimalPlaces { get; set; }
}

public sealed class UsageResponse
{
    [JsonPropertyName("limits")] public List<LimitEntry>? Limits { get; set; }
    [JsonPropertyName("extra_usage")] public ExtraUsage? ExtraUsage { get; set; }
}

/// <summary>What the rest of the app consumes after each poll.</summary>
public sealed class UsageSnapshot
{
    public required IReadOnlyList<LimitEntry> Limits { get; init; }
    public ExtraUsage? ExtraUsage { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;

    public LimitEntry? Worst =>
        Limits.Where(l => l.Percent is not null).MaxBy(l => l.Percent!.Value);
}
