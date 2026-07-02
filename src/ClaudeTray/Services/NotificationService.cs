using ClaudeTray.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ClaudeTray.Services;

/// <summary>
/// Edge-triggered toast alerts: one per bucket per limit window when it crosses the alert
/// threshold, an optional one at 100%, and an optional one when a window resets.
/// </summary>
public sealed class NotificationService
{
    private sealed class BucketState
    {
        public DateTimeOffset? WindowId;   // resets_at identifies the current limit window
        public bool ThresholdAlerted;
        public bool ExceededAlerted;
        public double LastPercent;
    }

    private readonly SettingsService _settings;
    private readonly Dictionary<string, BucketState> _state = new();

    public NotificationService(SettingsService settings) => _settings = settings;

    public void Process(UsageSnapshot snapshot)
    {
        var s = _settings.Current;
        if (s.MuteAll) { Track(snapshot); return; }

        foreach (var limit in snapshot.Limits)
        {
            if (limit.Percent is not double pct) continue;
            var st = _state.TryGetValue(limit.Key, out var existing) ? existing : _state[limit.Key] = new BucketState();
            var muted = s.MutedBuckets.Contains(limit.Key);

            // New window (resets_at moved forward) → re-arm and optionally announce the reset.
            if (st.WindowId is not null && limit.ResetsAt is not null && limit.ResetsAt > st.WindowId)
            {
                var wasAlerted = st.ThresholdAlerted;
                st.ThresholdAlerted = false;
                st.ExceededAlerted = false;
                if (s.NotifyOnReset && wasAlerted && !muted)
                    Toast($"{limit.Label} limit reset", $"Usage window restarted — now {pct:0}%.");
            }
            st.WindowId = limit.ResetsAt ?? st.WindowId;

            // Re-arm if usage dropped well below the threshold within the same window.
            if (st.ThresholdAlerted && pct < s.AlertThresholdPercent - 5) st.ThresholdAlerted = false;
            if (st.ExceededAlerted && pct < 95) st.ExceededAlerted = false;

            if (!muted)
            {
                if (s.NotifyAtExceeded && pct >= 100 && !st.ExceededAlerted)
                {
                    st.ExceededAlerted = true;
                    st.ThresholdAlerted = true;
                    Toast($"{limit.Label} limit reached",
                        $"You've hit 100% of this limit.{ResetSuffix(limit)}");
                }
                else if (s.NotifyAtThreshold && pct >= s.AlertThresholdPercent && !st.ThresholdAlerted)
                {
                    st.ThresholdAlerted = true;
                    Toast($"{limit.Label} at {pct:0}%",
                        $"Approaching your limit (alert set at {s.AlertThresholdPercent}%).{ResetSuffix(limit)}");
                }
            }

            st.LastPercent = pct;
        }
    }

    private void Track(UsageSnapshot snapshot)
    {
        foreach (var limit in snapshot.Limits)
        {
            if (limit.Percent is not double pct) continue;
            var st = _state.TryGetValue(limit.Key, out var existing) ? existing : _state[limit.Key] = new BucketState();
            st.WindowId = limit.ResetsAt ?? st.WindowId;
            st.LastPercent = pct;
        }
    }

    private static string ResetSuffix(LimitEntry limit) =>
        limit.ResetsAt is DateTimeOffset r ? $" Resets {FormatReset(r)}." : "";

    public static string FormatReset(DateTimeOffset resetsAt)
    {
        var local = resetsAt.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date) return local.ToString("HH:mm");
        if (local.Date == now.Date.AddDays(1)) return $"tomorrow {local:HH:mm}";
        return local.ToString("ddd HH:mm");
    }

    private static void Toast(string title, string body)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .Show();
        }
        catch
        {
            // Toasts can fail (focus assist, disabled notifications) — never crash over it.
        }
    }
}
