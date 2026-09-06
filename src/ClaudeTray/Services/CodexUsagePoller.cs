using ClaudeTray.Models;

namespace ClaudeTray.Services;

public enum CodexPollerState { Disabled, Waiting, Ok, Error }

/// <summary>Optional Codex account poller. It deliberately has no tray or notification consumers.</summary>
public sealed class CodexUsagePoller : IDisposable
{
    private readonly CodexUsageClient _client;
    private readonly SettingsService _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private Task? _loop;

    public CodexUsageSnapshot? Latest { get; private set; }
    public CodexPollerState State { get; private set; } = CodexPollerState.Disabled;
    public string? LastError { get; private set; }

    public event Action? Updated;

    public CodexUsagePoller(CodexUsageClient client, SettingsService settings)
    {
        _client = client;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
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
        if (!_settings.Current.ShowCodexLimits)
        {
            State = CodexPollerState.Disabled;
            Latest = null;
            LastError = null;
            Updated?.Invoke();
            return;
        }

        if (!await _pollLock.WaitAsync(0)) return;
        try
        {
            State = CodexPollerState.Waiting;
            Updated?.Invoke();
            try
            {
                Latest = await _client.FetchAsync(_cts.Token);
                State = CodexPollerState.Ok;
                LastError = null;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                State = CodexPollerState.Error;
                LastError = ex.Message;
            }
        }
        finally
        {
            _pollLock.Release();
        }
        Updated?.Invoke();
    }

    private void OnSettingsChanged() => _ = PollNowAsync();

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _cts.Cancel();
    }
}
