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
    private readonly CodexUsagePoller _codexPoller;
    private readonly SettingsService _settings;
    private readonly UpdateChecker _updates;

    public event Action? SettingsRequested;
    public event Action? SignInRequested;

    public FlyoutWindow(UsagePoller poller, CodexUsagePoller codexPoller, SettingsService settings, UpdateChecker updates)
    {
        InitializeComponent();
        _poller = poller;
        _codexPoller = codexPoller;
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

        Title = s.ShowCodexLimits ? "Account usage" : "Claude usage";
        TitleText.Text = Title;
        ClaudeSectionTitle.Visibility = s.ShowCodexLimits ? Visibility.Visible : Visibility.Collapsed;

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
            case PollerState.RateLimited:
                StatusText.Text = _poller.LastError ?? "Rate limited.";
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

        RefreshCodexContent(s);

        if (_updates.AvailableVersion is string v)
        {
            UpdateText.Text = $"Update available: v{v}";
            UpdateText.Visibility = Visibility.Visible;
        }
    }

    private void RefreshCodexContent(AppSettings settings)
    {
        CodexSection.Visibility = settings.ShowCodexLimits ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.ShowCodexLimits) return;

        CodexStatusText.Visibility = Visibility.Collapsed;
        CodexPlanText.Text = "";
        var snapshot = _codexPoller.Latest;
        if (snapshot?.PlanType is { Length: > 0 } plan)
            CodexPlanText.Text = char.ToUpperInvariant(plan[0]) + plan[1..];

        switch (_codexPoller.State)
        {
            case CodexPollerState.Waiting when snapshot is null:
                CodexStatusText.Text = "Reading limits from Codex…";
                CodexStatusText.Visibility = Visibility.Visible;
                break;
            case CodexPollerState.Error when snapshot is null:
                CodexStatusText.Text = _codexPoller.LastError ?? "Codex limits unavailable.";
                CodexStatusText.Visibility = Visibility.Visible;
                break;
            case CodexPollerState.Error:
                CodexStatusText.Text = $"Stale — last updated {snapshot.FetchedAt.ToLocalTime():HH:mm}";
                CodexStatusText.Visibility = Visibility.Visible;
                break;
        }

        if (_codexPoller.State == CodexPollerState.Ok && snapshot?.Limits.Count == 0)
        {
            CodexStatusText.Text = "No Codex limit windows were returned for this account.";
            CodexStatusText.Visibility = Visibility.Visible;
        }

        CodexBars.ItemsSource = snapshot?.Limits.Select(limit =>
        {
            var state = Severity.Classify(limit.Percent, settings);
            var pct = limit.Percent ?? 0;
            return new BarItem
            {
                Label = limit.Label,
                PercentText = limit.Percent is null ? "–" : $"{pct:0}%",
                BarHeight = Math.Clamp(pct / 100.0, 0.03, 1.0) * BarMaxHeight,
                BarBrush = new SolidColorBrush(Severity.WpfColor(state)),
                ResetText = limit.ResetsAt is { } reset ? $"↺ {NotificationService.FormatReset(reset)}" : "",
                Tooltip = $"Codex {limit.Label}: {pct:0}%" +
                          (limit.ResetsAt is { } reset2 ? $" — resets {NotificationService.FormatReset(reset2)}" : ""),
            };
        }).ToList() ?? [];
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

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await Task.WhenAll(_poller.PollNowAsync(), _codexPoller.PollNowAsync());

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
