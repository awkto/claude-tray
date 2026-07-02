using System.Windows;
using System.Windows.Controls;
using ClaudeTray.Services;

namespace ClaudeTray.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly List<CheckBox> _bucketBoxes = new();

    public SettingsWindow(SettingsService settings, AuthService auth, UsagePoller poller)
    {
        InitializeComponent();
        _settings = settings;
        _auth = auth;

        var s = settings.Current;
        ThresholdSlider.Value = s.AlertThresholdPercent;
        IntervalSlider.Value = s.PollIntervalSeconds;
        NotifyThresholdBox.IsChecked = s.NotifyAtThreshold;
        NotifyExceededBox.IsChecked = s.NotifyAtExceeded;
        NotifyResetBox.IsChecked = s.NotifyOnReset;
        MuteAllBox.IsChecked = s.MuteAll;
        UpdatesBox.IsChecked = s.CheckForUpdates;
        StartupBox.IsChecked = StartupManager.IsEnabled();
        SignOutButton.IsEnabled = auth.IsSignedIn;
        VersionText.Text = $"claude-tray v{UpdateChecker.CurrentVersion}";

        var buckets = poller.Latest?.Limits ?? [];
        NoBucketsText.Visibility = buckets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var limit in buckets)
        {
            var box = new CheckBox
            {
                Content = limit.Label,
                Tag = limit.Key,
                IsChecked = !s.MutedBuckets.Contains(limit.Key),
                Margin = new Thickness(0, 2, 0, 2),
            };
            _bucketBoxes.Add(box);
            BucketList.Items.Add(box);
        }
    }

    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdLabel is not null) ThresholdLabel.Text = $"{(int)ThresholdSlider.Value}%";
    }

    private void Interval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntervalLabel is not null) IntervalLabel.Text = $"{(int)IntervalSlider.Value}s";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings.Current;
        s.AlertThresholdPercent = (int)ThresholdSlider.Value;
        s.PollIntervalSeconds = (int)IntervalSlider.Value;
        s.NotifyAtThreshold = NotifyThresholdBox.IsChecked == true;
        s.NotifyAtExceeded = NotifyExceededBox.IsChecked == true;
        s.NotifyOnReset = NotifyResetBox.IsChecked == true;
        s.MuteAll = MuteAllBox.IsChecked == true;
        s.CheckForUpdates = UpdatesBox.IsChecked == true;
        s.MutedBuckets = _bucketBoxes.Where(b => b.IsChecked != true)
                                     .Select(b => (string)b.Tag)
                                     .ToHashSet();
        _settings.Save();
        StartupManager.SetEnabled(StartupBox.IsChecked == true);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _auth.SignOut();
        SignOutButton.IsEnabled = false;
    }
}
