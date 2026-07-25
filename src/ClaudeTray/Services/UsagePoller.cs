using ClaudeTray.Models;

namespace ClaudeTray.Services;

public enum PollerState { SignedOut, Ok, Error, RateLimited }

/// <summary>Polls the usage endpoint on the configured interval and broadcasts snapshots.</summary>
public sealed class UsagePoller : IDisposable
{
    private readonly UsageClient _client;
    private readonly AuthService _auth;
    private readonly SettingsService _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private Task? _loop;

    private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromMinutes(30);
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private int _backoffLevel;

    public UsageSnapshot? Latest { get; private set; }
    public PollerState State { get; private set; } = PollerState.SignedOut;
    public string? LastError { get; private set; }

    /// <summary>Raised on the thread pool after every poll attempt (success or failure).</summary>
    public event Action? Updated;

    public UsagePoller(UsageClient client, AuthService auth, SettingsService settings)
    {
        _client = client;
        _auth = auth;
        _settings = settings;
        _auth.StateChanged += () => _ = PollNowAsync();
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await PollNowAsync();
            try { await Task.Delay(_settings.Current.PollInterval, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task PollNowAsync()
    {
        if (!await _pollLock.WaitAsync(0)) return; // a poll is already in flight
        try
        {
            // A 429 costs us the next window too, so the backoff binds the loop and
            // both manual triggers ("Refresh now" and the flyout button) alike.
            if (DateTimeOffset.UtcNow < _backoffUntil) return;

            if (!_auth.IsSignedIn)
            {
                State = PollerState.SignedOut;
                Latest = null;
            }
            else
            {
                try
                {
                    Latest = await _client.FetchAsync(_cts.Token);
                    State = PollerState.Ok;
                    LastError = null;
                    _backoffLevel = 0;
                    _backoffUntil = DateTimeOffset.MinValue;
                }
                catch (OperationCanceledException) { return; }
                catch (AuthException ex)
                {
                    State = PollerState.SignedOut;
                    LastError = ex.Message;
                }
                catch (RateLimitException ex)
                {
                    // Keep the last snapshot — the bars stay roughly right — and back
                    // off instead of retrying straight back into the same window.
                    _backoffLevel = Math.Min(_backoffLevel + 1, 6);
                    var seconds = Math.Min(
                        Math.Max(ex.RetryAfter?.TotalSeconds ?? 0,
                                 BackoffBase.TotalSeconds * Math.Pow(2, _backoffLevel - 1)),
                        BackoffMax.TotalSeconds);
                    _backoffUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
                    State = PollerState.RateLimited;
                    LastError = $"Rate limited — retrying at {_backoffUntil.ToLocalTime():HH:mm}";
                }
                catch (Exception ex)
                {
                    // Network blip / 5xx / 429 — keep the last snapshot, mark stale.
                    State = PollerState.Error;
                    LastError = ex.Message;
                }
            }
        }
        finally
        {
            _pollLock.Release();
        }
        Updated?.Invoke();
    }

    public void Dispose() => _cts.Cancel();
}
