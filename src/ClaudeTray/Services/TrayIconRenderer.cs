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

    private static readonly Dictionary<UsageState, Icon> Cache = new();
    private static Bitmap? _mascot;

    /// <summary>Collapses bar-level severity to the three icon colors the tray uses.</summary>
    public static UsageState IconState(UsageState state) => state switch
    {
        UsageState.Warn => UsageState.Ok,
        UsageState.Exceeded => UsageState.Critical,
        _ => state,
    };

    public static Icon Render(UsageState state)
    {
        state = IconState(state);
        if (Cache.TryGetValue(state, out var cached)) return cached;

        _mascot ??= LoadMascot();
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            // Nearest-neighbor keeps the pixel-art edges crisp at 32px.
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_mascot, new Rectangle(0, 0, size, size));
        }

        var tint = Severity.GdiColor(state);
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

        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            var icon = (Icon)tmp.Clone();
            Cache[state] = icon; // cached for process lifetime — callers must not dispose
            return icon;
        }
        finally
        {
            DestroyIcon(hIcon);
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
