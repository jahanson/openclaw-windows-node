namespace OpenClawTray.Services;

public enum SpeechCaller { Chat, Node, Preview }
public sealed class SpeechPlaybackBusyException() : InvalidOperationException("Speech output is busy. Wait for the current playback to stop.");

/// <summary>Admission happens before synthesis so another caller never incurs a charge for queued audio.</summary>
public sealed class SpeechPlaybackArbiter
{
    public static SpeechPlaybackArbiter Shared { get; } = new();
    private readonly object _gate = new();
    private SpeechPlaybackLease? _current;

    public async Task<SpeechPlaybackLease> AcquireAsync(SpeechCaller caller, bool interrupt, CancellationToken cancellationToken)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        admission.CancelAfter(TimeSpan.FromSeconds(2));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpeechPlaybackLease? previous;
            lock (_gate)
            {
                previous = _current;
                if (previous is null)
                    return _current = new SpeechPlaybackLease(this, caller, cancellationToken);
                if (previous.Caller != caller || !interrupt) throw new SpeechPlaybackBusyException();
            }
            previous.Cancel();
            // Cancellation is a request, not proof that the output device has been relinquished.
            try { await previous.Released.WaitAsync(admission.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new SpeechPlaybackBusyException(); }
        }
    }

    public void Stop(SpeechCaller caller)
    {
        SpeechPlaybackLease? lease;
        lock (_gate) lease = _current?.Caller == caller ? _current : null;
        lease?.Cancel();
    }

    internal void Release(SpeechPlaybackLease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, lease)) _current = null;
        }
    }
}

public sealed class SpeechPlaybackLease : IDisposable
{
    private readonly SpeechPlaybackArbiter _owner;
    private readonly CancellationTokenSource _lifetime;
    private int _disposed;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Released => _released.Task;
    public SpeechCaller Caller { get; }
    public CancellationToken Token { get; }

    internal SpeechPlaybackLease(SpeechPlaybackArbiter owner, SpeechCaller caller, CancellationToken token)
    {
        _owner = owner;
        Caller = caller;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Token = _lifetime.Token;
    }

    internal void Cancel()
    {
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner.Release(this);
        _lifetime.Dispose();
        _released.TrySetResult();
    }
}
