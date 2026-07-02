using System.Windows;
using ClaudeTray.Services;

namespace ClaudeTray.UI;

public partial class SignInWindow : Window
{
    private readonly AuthService _auth;
    private CancellationTokenSource? _signInCts;

    public SignInWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        Loaded += async (_, _) => await ScanCredentialsAsync();
        Closed += (_, _) => _signInCts?.Cancel();
    }

    private async Task ScanCredentialsAsync()
    {
        var paths = await Task.Run(AuthService.DiscoverCredentialFiles);
        if (paths.Count == 0)
        {
            ScanText.Text = "No Claude Code credential files found on Windows or in WSL.";
            return;
        }
        ScanText.Text = $"Found {paths.Count} credential file{(paths.Count == 1 ? "" : "s")}:";
        foreach (var p in paths) CredentialPaths.Items.Add(p);
        CredentialPaths.SelectedIndex = 0;
        ImportButton.IsEnabled = true;
    }

    private async void Browser_Click(object sender, RoutedEventArgs e)
    {
        BrowserButton.IsEnabled = false;
        StatusText.Text = "Waiting for the browser sign-in to complete…";
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await _auth.SignInWithBrowserAsync(_signInCts.Token);
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Sign-in timed out or was cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            BrowserButton.IsEnabled = true;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _auth.ImportCredentialsFile((string)CredentialPaths.SelectedItem);
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccessTokenBox.Text))
        {
            StatusText.Text = "Access token is required.";
            return;
        }
        _auth.ImportPastedTokens(AccessTokenBox.Text, RefreshTokenBox.Text);
        Close();
    }
}
