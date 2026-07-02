using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using ClaudeTray.Services;
using ClaudeTray.UI;
using H.NotifyIcon;

namespace ClaudeTray;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TaskbarIcon? _tray;
    private System.Drawing.Icon? _currentIcon;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private SettingsService _settings = null!;
    private AuthService _auth = null!;
    private UsagePoller _poller = null!;
    private NotificationService _notifications = null!;
    private UpdateChecker _updates = null!;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private SignInWindow? _signInWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\claude-tray-single-instance", out var isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _settings = new SettingsService();
        _settings.Load();
        _auth = new AuthService(_http);
        _auth.Initialize();
        _poller = new UsagePoller(new UsageClient(_http, _auth), _auth, _settings);
        _notifications = new NotificationService(_settings);
        _updates = new UpdateChecker(_http, _settings);

        CreateTrayIcon();
        _poller.Updated += OnPollerUpdated;
        _updates.UpdateFound += () => Dispatcher.Invoke(UpdateTrayVisuals);
        _poller.Start();

        if (!_auth.IsSignedIn)
            Dispatcher.InvokeAsync(ShowSignIn);
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("Open", (_, _) => ShowFlyout()));
        menu.Items.Add(MakeItem("Refresh now", async (_, _) => await _poller.PollNowAsync()));
        menu.Items.Add(MakeItem("Settings…", (_, _) => ShowSettings()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Quit", (_, _) => Shutdown()));

        _tray = new TaskbarIcon
        {
            ToolTipText = "claude-tray — waiting for first poll…",
            ContextMenu = menu,
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowFlyout();
        SetTrayIcon(TrayIconRenderer.Render(null, UsageState.Stale));
        _tray.ForceCreate();
    }

    private static MenuItem MakeItem(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private void OnPollerUpdated()
    {
        var snapshot = _poller.Latest;
        if (snapshot is not null && _poller.State == PollerState.Ok)
            _notifications.Process(snapshot);

        Dispatcher.Invoke(() =>
        {
            UpdateTrayVisuals();
            if (_flyout?.IsVisible == true) _flyout.RefreshContent();
        });

        _ = _updates.MaybeCheckAsync(CancellationToken.None);
    }

    private void UpdateTrayVisuals()
    {
        if (_tray is null) return;
        var s = _settings.Current;
        var snapshot = _poller.Latest;

        switch (_poller.State)
        {
            case PollerState.SignedOut:
                SetTrayIcon(TrayIconRenderer.Render(null, UsageState.Stale));
                _tray.ToolTipText = "claude-tray — not signed in";
                return;
            case PollerState.Error when snapshot is null:
                SetTrayIcon(TrayIconRenderer.Render(null, UsageState.Stale));
                _tray.ToolTipText = "claude-tray — can't reach Anthropic";
                return;
        }
        if (snapshot is null) return;

        var worst = snapshot.Worst;
        var state = Severity.Classify(worst?.Percent, s);
        SetTrayIcon(TrayIconRenderer.Render(worst?.Percent, state));

        var parts = snapshot.Limits
            .Where(l => l.Percent is not null)
            .Select(l => $"{l.ShortLabel} {l.Percent:0}%");
        var tip = string.Join(" · ", parts);
        _tray.ToolTipText = string.IsNullOrEmpty(tip) ? "claude-tray" :
            _poller.State == PollerState.Error ? $"{tip} (stale)" : tip;
    }

    private void SetTrayIcon(System.Drawing.Icon icon)
    {
        _tray!.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    private void ShowFlyout()
    {
        _flyout ??= CreateFlyout();
        _flyout.ShowNearTray();
    }

    private FlyoutWindow CreateFlyout()
    {
        var flyout = new FlyoutWindow(_poller, _settings, _updates);
        flyout.SettingsRequested += ShowSettings;
        flyout.SignInRequested += ShowSignIn;
        return flyout;
    }

    private void ShowSettings()
    {
        if (_settingsWindow?.IsVisible == true)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _auth, _poller);
        _settingsWindow.Show();
    }

    private void ShowSignIn()
    {
        if (_signInWindow?.IsVisible == true)
        {
            _signInWindow.Activate();
            return;
        }
        _signInWindow = new SignInWindow(_auth);
        _signInWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _poller?.Dispose();
        _tray?.Dispose();
        _currentIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
