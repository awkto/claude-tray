using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudeTray.Services;

/// <summary>
/// Draws the tray icon at runtime: a rounded vertical gauge filled to the worst bucket's
/// percentage, colored by severity, with an "!" badge when a limit is exceeded.
/// </summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Render(double? percent, UsageState state)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var barRect = new Rectangle(9, 2, 14, 28);
            using (var track = RoundedRect(barRect, 6))
            using (var trackBrush = new SolidBrush(Color.FromArgb(90, 128, 128, 128)))
                g.FillPath(trackBrush, track);

            if (percent is double p)
            {
                var frac = Math.Clamp(p / 100.0, 0.04, 1.0);
                var fillHeight = (int)Math.Round(barRect.Height * frac);
                var fillRect = new Rectangle(barRect.X, barRect.Bottom - fillHeight, barRect.Width, fillHeight);
                using var clip = RoundedRect(barRect, 6);
                g.SetClip(clip);
                using var fillBrush = new SolidBrush(Severity.GdiColor(state));
                g.FillRectangle(fillBrush, fillRect);
                g.ResetClip();
            }

            using (var outline = RoundedRect(barRect, 6))
            using (var pen = new Pen(Color.FromArgb(160, 255, 255, 255), 1.5f))
                g.DrawPath(pen, outline);

            if (state == UsageState.Exceeded)
            {
                var badge = new Rectangle(18, 16, 14, 14);
                using var badgeBrush = new SolidBrush(Color.FromArgb(0xEF, 0x44, 0x44));
                using var badgePen = new Pen(Color.White, 1.5f);
                g.FillEllipse(badgeBrush, badge);
                g.DrawEllipse(badgePen, badge);
                using var font = new Font("Segoe UI", 8, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("!", font, Brushes.White, new RectangleF(badge.X, badge.Y, badge.Width, badge.Height), fmt);
            }
        }

        // Icon.FromHandle doesn't own the handle; clone into a managed Icon then release it.
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
