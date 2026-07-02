using System.Windows.Media;

namespace ClaudeTray.Services;

public enum UsageState { Ok, Warn, High, Critical, Exceeded, Stale }

public static class Severity
{
    public static UsageState Classify(double? percent, AppSettings s)
    {
        if (percent is not double p) return UsageState.Stale;
        if (p >= 100) return UsageState.Exceeded;
        if (p >= s.AlertThresholdPercent) return UsageState.Critical;
        if (p >= s.HighThresholdPercent) return UsageState.High;
        if (p >= s.WarnThresholdPercent) return UsageState.Warn;
        return UsageState.Ok;
    }

    public static Color WpfColor(UsageState state) => state switch
    {
        UsageState.Ok => Color.FromRgb(0x22, 0xC5, 0x5E),
        UsageState.Warn => Color.FromRgb(0xEA, 0xB3, 0x08),
        UsageState.High => Color.FromRgb(0xF9, 0x73, 0x16),
        UsageState.Critical => Color.FromRgb(0xEF, 0x44, 0x44),
        UsageState.Exceeded => Color.FromRgb(0xEF, 0x44, 0x44),
        _ => Color.FromRgb(0x9C, 0xA3, 0xAF),
    };

    public static System.Drawing.Color GdiColor(UsageState state)
    {
        var c = WpfColor(state);
        return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
    }
}
