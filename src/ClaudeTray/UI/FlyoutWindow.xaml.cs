using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using ClaudeTray.Services;

namespace ClaudeTray.UI;

public partial class FlyoutWindow : Window
{
    public sealed class BarItem
    {
        public required string Label { get; init; }
        public required string PercentText { get; init; }
        public required double BarHeight { get; init; }
        public required Brush BarBrush { get; init; }
        public required string ResetText { get; init; }
        public required string Tooltip { get; init; }
    }

    private const double BarMaxHeight = 92;

    private readonly UsagePoller _poller;
    private readonly SettingsService _settings;
    private readonly UpdateChecker _updates;

    public event Action? SettingsRequested;
    public event Action? SignInRequested;

    public FlyoutWindow(UsagePoller poller, SettingsService settings, UpdateChecker updates)
    {
        InitializeComponent();
        _poller = poller;
        _settings = settings;
        _updates = updates;
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
    }

    public void ShowNearTray()
    {
        RefreshContent();
        Show();
        Activate();
        PositionNearCursor();
    }

    public void RefreshContent()
    {
        var s = _settings.Current;
        var snapshot = _poller.Latest;

        SignInButton.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;

        switch (_poller.State)
        {
            case PollerState.SignedOut:
                StatusText.Text = _poller.LastError ?? "Not signed in.";
                StatusText.Visibility = Visibility.Visible;
                SignInButton.Visibility = Visibility.Visible;
                break;
            case PollerState.Error when snapshot is null:
                StatusText.Text = $"Can't reach Anthropic: {_poller.LastError}";
                StatusText.Visibility = Visibility.Visible;
                break;
            case PollerState.Error:
                StatusText.Text = $"Stale — last updated {snapshot.FetchedAt.ToLocalTime():HH:mm}";
                StatusText.Visibility = Visibility.Visible;
                break;
        }

        var items = new List<BarItem>();
        if (snapshot is not null)
        {
            foreach (var limit in snapshot.Limits)
            {
                var state = Severity.Classify(limit.Percent, s);
                var pct = limit.Percent ?? 0;
                items.Add(new BarItem
                {
                    Label = limit.ShortLabel,
                    PercentText = limit.Percent is null ? "–" : $"{pct:0}%",
                    BarHeight = Math.Clamp(pct / 100.0, 0.03, 1.0) * BarMaxHeight,
                    BarBrush = new SolidColorBrush(Severity.WpfColor(state)),
                    ResetText = limit.ResetsAt is DateTimeOffset r ? $"↺ {NotificationService.FormatReset(r)}" : "",
                    Tooltip = $"{limit.Label}: {pct:0}%" +
                              (limit.ResetsAt is DateTimeOffset r2 ? $" — resets {NotificationService.FormatReset(r2)}" : ""),
                });
            }

            var extra = snapshot.ExtraUsage;
            if (extra?.IsEnabled == true && extra.MonthlyLimit is double lim)
            {
                var div = Math.Pow(10, extra.DecimalPlaces ?? 2);
                FooterText.Text = $"Extra usage: {(extra.UsedCredits ?? 0) / div:0.##} / {lim / div:0.##} {extra.Currency}";
                FooterText.Visibility = Visibility.Visible;
            }
            else
            {
                FooterText.Visibility = Visibility.Collapsed;
            }
        }
        Bars.ItemsSource = items;

        if (_updates.AvailableVersion is string v)
        {
            UpdateText.Text = $"Update available: v{v}";
            UpdateText.Visibility = Visibility.Visible;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PixelPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelPoint { public int X; public int Y; }

    private void PositionNearCursor()
    {
        GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var cx = cursor.X / dpi.DpiScaleX;
        var cy = cursor.Y / dpi.DpiScaleY;
        var wa = SystemParameters.WorkArea;

        // Anchor toward the cursor (tray icon), clamped to the monitor work area —
        // handles bottom/top/left/right taskbars without special cases.
        Left = Math.Max(wa.Left, Math.Min(cx - ActualWidth / 2, wa.Right - ActualWidth));
        Top = cy > wa.Top + wa.Height / 2
            ? Math.Max(wa.Top, wa.Bottom - ActualHeight)
            : Math.Min(wa.Bottom - ActualHeight, wa.Top);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _poller.PollNowAsync();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        SignInRequested?.Invoke();
    }

    private void Update_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_updates.ReleaseUrl is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
