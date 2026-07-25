using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ClaudeTray.Services;

/// <summary>
/// Tray icon = the Clawd mascot (from Clawdmeter, MIT) tinted by overall state:
/// green (all limits under the high threshold), orange (any at/over it),
/// red (any at/over the alert threshold), gray (signed out / stale).
/// </summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<(UsageState State, int? Percent), Icon> Cache = new();
    private static Bitmap? _mascot;

    /// <summary>Collapses bar-level severity to the three icon colors the tray uses.</summary>
    public static UsageState IconState(UsageState state) => state switch
    {
        UsageState.Warn => UsageState.Ok,
        UsageState.Exceeded => UsageState.Critical,
        _ => state,
    };

    /// <param name="percent">When set, the digits replace the mascot. A tray icon is a
    /// single 16px-ish bitmap with no room for both — two legible digits need the whole
    /// canvas — so this is the "show percentage" mode rather than a badge.</param>
    public static Icon Render(UsageState state, int? percent = null)
    {
        state = IconState(state);
        if (Cache.TryGetValue((state, percent), out var cached)) return cached;

        const int size = 32;
        using var bmp = new Bitmap(size, size);
        var tint = Severity.GdiColor(state);

        if (percent is { } pct)
        {
            using var g = Graphics.FromImage(bmp);
            DrawPercent(g, size, pct, tint);
        }
        else
        {
            _mascot ??= LoadMascot();
            using (var g = Graphics.FromImage(bmp))
            {
                // Nearest-neighbor keeps the pixel-art edges crisp at 32px.
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_mascot, new Rectangle(0, 0, size, size));
            }

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.A == 0) continue;
                    // Body pixels get the state color; the near-black eyes stay black.
                    if (p.R + p.G + p.B > 150)
                        bmp.SetPixel(x, y, Color.FromArgb(p.A, tint));
                }
            }
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            var icon = (Icon)tmp.Clone();
            // Cached for process lifetime — callers must not dispose. Bounded at
            // 4 states x 101 percentages, and in practice only a handful are ever hit.
            Cache[(state, percent)] = icon;
            return icon;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>Digits centred and auto-fitted, so 100% still renders rather than clipping.</summary>
    private static void DrawPercent(Graphics g, int size, int percent, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var text = Math.Clamp(percent, 0, 999).ToString();
        for (var px = 30f; px >= 9f; px -= 1f)
        {
            using var font = new Font(FontFamily.GenericSansSerif, px, FontStyle.Bold, GraphicsUnit.Pixel);
            var m = g.MeasureString(text, font);
            if (m.Width > size) continue;

            var x = (size - m.Width) / 2f;
            var y = (size - m.Height) / 2f;
            // Dark halo so the digits stay legible on light taskbars too.
            using (var halo = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
            {
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            g.DrawString(text, font, halo, x + dx, y + dy);
            }
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, x, y);
            return;
        }
    }

    private static Bitmap LoadMascot()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("ClaudeTray.Assets.clawd.png")
                           ?? throw new InvalidOperationException("Embedded mascot resource missing.");
        return new Bitmap(stream);
    }
}
