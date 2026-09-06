using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ClaudeTray.Models;

namespace ClaudeTray.Services;

/// <summary>
/// Reads ChatGPT account limits through the user's installed Codex CLI. The app-server
/// owns authentication and token refresh; claude-tray never reads or stores OpenAI tokens.
/// </summary>
public sealed class CodexUsageClient
{
    private sealed record Command(string FileName, params string[] Arguments);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<CodexUsageSnapshot> FetchAsync(CancellationToken ct)
    {
        var commands = OperatingSystem.IsWindows()
            ? new[]
            {
                new Command("codex.exe", "app-server"),
                // npm installs expose codex.cmd rather than codex.exe on PATH.
                new Command("cmd.exe", "/d", "/s", "/c", "codex app-server"),
                new Command("wsl.exe", "-e", "codex", "app-server"),
            }
            : new[] { new Command("codex", "app-server") };

        var errors = new List<string>();
        foreach (var command in commands)
        {
            try
            {
                return await FetchWithCommandAsync(command, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is Win32Exception or CodexUsageException or IOException)
            {
                errors.Add(ex.Message);
            }
        }

        throw new CodexUsageException(
            "Codex limits unavailable. Install Codex and sign in with ChatGPT. " +
            string.Join(" ", errors.Distinct()));
    }

    private static async Task<CodexUsageSnapshot> FetchWithCommandAsync(Command command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command.Arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
                            ?? throw new CodexUsageException($"Could not start {command.FileName}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await SendAsync(process, new
            {
                method = "initialize",
                id = 0,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "claude_tray",
                        title = "claude-tray",
                        version = UpdateChecker.CurrentVersion,
                    },
                },
            });
            using var initialization = await ReadResultAsync(process, 0, timeout.Token);

            await SendAsync(process, new { method = "initialized", @params = new { } });
            await SendAsync(process, new { method = "account/rateLimits/read", id = 1 });
            using var result = await ReadResultAsync(process, 1, timeout.Token);

            var parsed = result.RootElement.Deserialize<CodexRateLimitsResult>(JsonOpts)
                         ?? throw new CodexUsageException("Codex returned an empty limits response.");
            return ToSnapshot(parsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CodexUsageException("Codex did not return account limits within 15 seconds.");
        }
        catch (CodexUsageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new CodexUsageException($"Could not read the Codex limits response: {ex.Message}");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* process already exited */ }

            try
            {
                var stderr = await stderrTask;
                if (process.HasExited && process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                    Debug.WriteLine($"Codex app-server: {stderr.Trim()}");
            }
            catch { /* cancellation / closed process */ }
        }
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument> ReadResultAsync(Process process, int id, CancellationToken ct)
    {
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                continue;

            if (root.TryGetProperty("error", out var error))
            {
                var errorMessage = error.TryGetProperty("message", out var text) ? text.GetString() : error.ToString();
                throw new CodexUsageException(errorMessage ?? "Codex returned an error.");
            }
            if (!root.TryGetProperty("result", out var result))
                throw new CodexUsageException("Codex response did not contain a result.");
            return JsonDocument.Parse(result.GetRawText());
        }

        string detail;
        try
        {
            await process.WaitForExitAsync(ct);
            detail = $"Codex app-server exited with code {process.ExitCode}.";
        }
        catch
        {
            detail = "Codex app-server closed before returning account limits.";
        }
        throw new CodexUsageException(detail);
    }

    private static CodexUsageSnapshot ToSnapshot(CodexRateLimitsResult result)
    {
        var buckets = result.RateLimitsByLimitId?.Values.ToList();
        if (buckets is not { Count: > 0 } && result.RateLimits is not null)
            buckets = [result.RateLimits];

        var multipleBuckets = buckets is { Count: > 1 };
        var limits = new List<CodexLimitEntry>();
        foreach (var bucket in buckets ?? [])
        {
            AddWindow(limits, bucket, bucket.Primary, "primary", multipleBuckets);
            AddWindow(limits, bucket, bucket.Secondary, "secondary", multipleBuckets);
        }

        return new CodexUsageSnapshot
        {
            Limits = limits,
            PlanType = buckets?.Select(b => b.PlanType).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
        };
    }

    private static void AddWindow(
        ICollection<CodexLimitEntry> limits,
        CodexRateLimit bucket,
        CodexRateLimitWindow? window,
        string windowName,
        bool multipleBuckets)
    {
        if (window is null || (window.UsedPercent is null && window.ResetsAt is null)) return;

        var duration = FormatDuration(window.WindowDurationMins);
        var bucketLabel = !string.IsNullOrWhiteSpace(bucket.LimitName)
            ? bucket.LimitName!
            : Humanize(bucket.LimitId);
        var showBucket = multipleBuckets || !bucket.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase);

        limits.Add(new CodexLimitEntry
        {
            Key = $"{bucket.LimitId}:{windowName}",
            Label = showBucket ? $"{bucketLabel} · {duration}" : duration,
            Percent = window.UsedPercent,
            ResetsAt = window.ResetsAt is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt.Value)
                : null,
        });
    }

    private static string FormatDuration(int? minutes) => minutes switch
    {
        null or <= 0 => "Limit",
        10080 => "Week",
        >= 1440 when minutes % 1440 == 0 => $"{minutes / 1440} days",
        >= 60 when minutes % 60 == 0 => $"{minutes / 60} hours",
        _ => $"{minutes} min",
    };

    private static string Humanize(string value)
    {
        var words = value.Replace('_', ' ').Replace('-', ' ');
        return words.Length == 0 ? "Codex" : char.ToUpperInvariant(words[0]) + words[1..];
    }
}

public sealed class CodexUsageException : Exception
{
    public CodexUsageException(string message) : base(message) { }
}
