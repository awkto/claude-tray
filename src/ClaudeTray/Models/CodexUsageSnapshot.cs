using System.Text.Json.Serialization;

namespace ClaudeTray.Models;

public sealed class CodexRateLimitWindow
{
    [JsonPropertyName("usedPercent")] public double? UsedPercent { get; set; }
    [JsonPropertyName("windowDurationMins")] public int? WindowDurationMins { get; set; }
    [JsonPropertyName("resetsAt")] public long? ResetsAt { get; set; }
}

public sealed class CodexRateLimit
{
    [JsonPropertyName("limitId")] public string LimitId { get; set; } = "codex";
    [JsonPropertyName("limitName")] public string? LimitName { get; set; }
    [JsonPropertyName("primary")] public CodexRateLimitWindow? Primary { get; set; }
    [JsonPropertyName("secondary")] public CodexRateLimitWindow? Secondary { get; set; }
    [JsonPropertyName("planType")] public string? PlanType { get; set; }
}

public sealed class CodexRateLimitsResult
{
    [JsonPropertyName("rateLimits")] public CodexRateLimit? RateLimits { get; set; }
    [JsonPropertyName("rateLimitsByLimitId")]
    public Dictionary<string, CodexRateLimit>? RateLimitsByLimitId { get; set; }
}

public sealed class CodexLimitEntry
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public double? Percent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}

public sealed class CodexUsageSnapshot
{
    public required IReadOnlyList<CodexLimitEntry> Limits { get; init; }
    public string? PlanType { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
}
