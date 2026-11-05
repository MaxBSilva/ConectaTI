namespace ConectaTI.App.Editing;

// Owns request ordering and timed visibility only; no plaintext and no UI objects.
internal sealed class PasswordRevealLease
{
    private long _request;
    private CancellationTokenSource? _expiration;
    public Func<bool> BeginRequest(Func<bool> editorCurrent)
    {
        var request = ++_request;
        return () => request == _request && editorCurrent();
    }
    public void Cancel()
    {
        _request++;
        _expiration?.Cancel(); _expiration?.Dispose(); _expiration = null;
    }
    public async Task ScheduleAutoHideAsync(TimeSpan delay, Func<CancellationToken, Task> onExpiry)
    {
        _expiration?.Cancel(); _expiration?.Dispose();
        using var expiration = new CancellationTokenSource();
        _expiration = expiration;
        var token = expiration.Token;
        try
        {
            await Task.Delay(delay, token);
            if (!token.IsCancellationRequested) await onExpiry(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_expiration, expiration)) _expiration = null; }
    }
}
