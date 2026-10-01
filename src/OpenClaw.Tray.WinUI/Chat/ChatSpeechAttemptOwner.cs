using OpenClawTray.Services;
using OpenClaw.Shared.Speech;

namespace OpenClawTray.Chat;

public sealed record SpeechSessionIdentity(string GatewayId, long ConnectionGeneration,
    string SessionKey, string SessionIncarnation);
public sealed record SpeechAttemptRequest(string GatewayId, long ConnectionGeneration,
    SpeechRenditionIdentity Identity, bool Automatic, string CueFormat = "elevenlabs-audio-tags", string Origin = "composed")
{
    public SpeechSessionIdentity Session => new(GatewayId, ConnectionGeneration, Identity.SessionKey, Identity.SessionIncarnation);
}
public sealed record SpeechPlaybackRequest(SpeechAttemptRequest Attempt, string? PreparedText,
    IAsyncEnumerable<string>? LiveText, Action<PcmPlaybackPhase>? ReportPhase = null);
public enum SpeechAttemptOutcome { Completed, Cancelled, Skipped, Failed }
public sealed record SpeechAttemptResult(SpeechAttemptOutcome Outcome, string? Reason = null);
public sealed record SpeechAttemptStatus(long Generation, SpeechAttemptRequest? Attempt, string State,
    string? EffectiveMode = null, string? Reason = null, bool PlaybackStarted = false);

/// <summary>
/// One foreground chat playback lifetime, independent from written-answer generation and transcript storage.
/// Session identity is supplied by the conversation owner; this class never reconstructs it from text.
/// </summary>
public sealed class ChatSpeechAttemptOwner : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<SpeechPlaybackRequest, CancellationToken, Task> _execute;
    private readonly Func<IDisposable> _muteCapture;
    private readonly SpeechPlaybackArbiter _arbiter;
    private readonly HashSet<(SpeechSessionIdentity, string, string)> _consumed = [];
    private SpeechSessionIdentity? _foreground;
    private CancellationTokenSource? _active;
    private bool _automaticMuted;
    private bool _available;
    private bool _disposed;
    private long _generation;
    private SpeechAttemptStatus _status = new(0, null, "unavailable");

    public ChatSpeechAttemptOwner(Func<SpeechPlaybackRequest, CancellationToken, Task> execute,
        Func<IDisposable> muteCapture, SpeechPlaybackArbiter arbiter)
    {
        _execute = execute;
        _muteCapture = muteCapture;
        _arbiter = arbiter;
    }

    public SpeechAttemptStatus Status { get { lock (_gate) return _status; } }
    public bool IsManualPlaybackActive { get { lock (_gate) return _active is not null && _status.Attempt?.Automatic == false; } }
    public event EventHandler? StatusChanged;

    public void SetAvailable(bool available)
    {
        CancellationTokenSource? previous = null;
        var changed = false;
        lock (_gate)
        {
            _available = available;
            if (!available)
            {
                previous = DetachLocked("unavailable", clearReadiness: true);
                changed = true;
            }
        }
        Cancel(previous);
        if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetComposing(string effectiveMode, string? reason = null)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _available = true;
            previous = _active;
            _active = null;
            _status = new(++_generation, null, "composing", effectiveMode, reason);
        }
        Cancel(previous);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetUnavailable(string reason)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _available = false;
            previous = DetachLocked("unavailable", clearReadiness: true, reason: reason);
        }
        Cancel(previous);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetForeground(SpeechSessionIdentity? identity)
    {
        CancellationTokenSource? previous = null;
        var changed = false;
        lock (_gate)
        {
            if (_foreground != identity)
            {
                previous = DetachLocked("session-changed", clearReadiness: true);
                changed = true;
            }
            _foreground = identity;
        }
        Cancel(previous);
        if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetAutomaticMuted(bool muted)
    {
        CancellationTokenSource? previous = null;
        var changed = false;
        lock (_gate)
        {
            _automaticMuted = muted;
            if (muted && _status.Attempt?.Automatic == true)
            {
                previous = DetachLocked("muted");
                changed = true;
            }
        }
        Cancel(previous);
        if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Invalidate(string reason, SpeechSessionIdentity? session = null)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (session is not null && _status.Attempt?.Session != session) return;
            previous = DetachLocked(reason);
        }
        Cancel(previous);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetTerminalStatus(string state, string reason)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _active;
            _active = null;
            _status = new(++_generation, _status.Attempt, state, _status.EffectiveMode,
                reason, _status.PlaybackStarted || _status.State == "playing");
        }
        Cancel(previous);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private CancellationTokenSource? DetachLocked(string state, bool clearReadiness = false, string? reason = null)
    {
        ++_generation;
        var previous = _active;
        _active = null;
        _status = new(_generation, _status.Attempt, state,
            clearReadiness ? null : _status.EffectiveMode,
            clearReadiness ? reason : _status.Reason,
            _status.PlaybackStarted || _status.State == "playing");
        return previous;
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public Task<SpeechAttemptResult> PlayPreparedAsync(SpeechAttemptRequest request, string text,
        CancellationToken cancellationToken = default) => RunAsync(new(request, text, null), cancellationToken);

    public Task<SpeechAttemptResult> PlayLiveAsync(SpeechAttemptRequest request, IAsyncEnumerable<string> text,
        CancellationToken cancellationToken = default) => RunAsync(new(request, null, text), cancellationToken);

    private async Task<SpeechAttemptResult> RunAsync(SpeechPlaybackRequest work, CancellationToken cancellationToken)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource lifetime;
        long generation;
        lock (_gate)
        {
            if (_disposed || !_available) return new(SpeechAttemptOutcome.Skipped, "Dialog is unavailable.");
            if (_foreground != work.Attempt.Session) return new(SpeechAttemptOutcome.Skipped, "This session is not foreground.");
            if (work.Attempt.Automatic && _automaticMuted) return new(SpeechAttemptOutcome.Skipped, "Automatic speech is muted.");
            var key = (work.Attempt.Session, work.Attempt.Identity.ResponseId, work.Attempt.Identity.RenditionId);
            if (work.Attempt.Automatic && _consumed.Contains(key)) return new(SpeechAttemptOutcome.Skipped, "This rendition was already consumed.");
            // Keep dedupe state bounded without accidentally replaying an evicted response.
            if (work.Attempt.Automatic && _consumed.Count >= 1024) return new(SpeechAttemptOutcome.Skipped, "Automatic speech history limit reached. Replay manually.");
            if (work.Attempt.Automatic) _consumed.Add(key);
            previous = _active;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _active = lifetime;
            generation = ++_generation;
            var pacedWritten = work.Attempt.Origin == "written-fallback"
                && WrittenAnswerSpeechPlayback.RequiresStreaming(work.PreparedText ?? "");
            var effectiveMode = work.LiveText is null && !pacedWritten ? "prepared" : "live";
            _status = new(generation, work.Attempt, "synthesizing", effectiveMode,
                pacedWritten ? "written-answer-streamed" : work.Attempt.Automatic ? _status.Reason : null);
        }
        Cancel(previous);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        SpeechPlaybackLease? lease = null;
        IDisposable? mute = null;
        Task? execution = null;
        try
        {
            lease = await _arbiter.AcquireAsync(SpeechCaller.Chat, interrupt: true, lifetime.Token).ConfigureAwait(false);
            mute = _muteCapture();
            execution = _execute(work with { ReportPhase = phase =>
            {
                lock (_gate)
                {
                    if (_generation != generation || _active is null) return;
                    // Concurrent producer callbacks cannot regress already-started output to buffering.
                    if (_status.State == "playing" && phase == PcmPlaybackPhase.Buffering) return;
                    _status = _status with
                    {
                        State = phase == PcmPlaybackPhase.Playing ? "playing" : "buffering",
                        PlaybackStarted = _status.PlaybackStarted || phase == PcmPlaybackPhase.Playing
                    };
                }
                StatusChanged?.Invoke(this, EventArgs.Empty);
            } }, lease.Token);
            await execution.WaitAsync(lease.Token).ConfigureAwait(false);
            lease.Token.ThrowIfCancellationRequested();
            return Finish(SpeechAttemptOutcome.Completed, "completed");
        }
        catch (OperationCanceledException) { return Finish(SpeechAttemptOutcome.Cancelled, "cancelled"); }
        catch (SpeechPlaybackBusyException) { return Finish(SpeechAttemptOutcome.Failed, "failed", "playback-busy"); }
        catch (WrittenAnswerReadingException error) { return Finish(SpeechAttemptOutcome.Failed, "failed", error.Reason); }
        catch (DialogProviderException error) { return Finish(SpeechAttemptOutcome.Failed, "failed", ProviderReason(error.Reason)); }
        catch (Exception) { return Finish(SpeechAttemptOutcome.Failed, "failed", "playback-failed"); }
        finally
        {
            if (execution is { IsCompleted: false })
            {
                // Stop is prompt, but retain device ownership and capture muting until teardown really finishes.
                _ = execution.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    try { mute?.Dispose(); } finally { lease?.Dispose(); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                _ = execution?.Exception;
                try { mute?.Dispose(); } finally { lease?.Dispose(); }
            }
            lock (_gate) { if (ReferenceEquals(_active, lifetime)) _active = null; }
            lifetime.Dispose();
        }

        SpeechAttemptResult Finish(SpeechAttemptOutcome outcome, string state, string? reason = null)
        {
            lock (_gate)
            {
                if (_generation != generation) return new(SpeechAttemptOutcome.Cancelled);
                _status = _status with { State = state, Reason = reason ?? _status.Reason };
            }
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return new(outcome, outcome == SpeechAttemptOutcome.Failed ? reason : null);
        }
    }

    private static string ProviderReason(DialogProviderFailure reason) => reason switch
    {
        DialogProviderFailure.Authentication => "provider-authentication",
        DialogProviderFailure.Configuration => "provider-configuration",
        DialogProviderFailure.AccountRestriction => "provider-account-restriction",
        DialogProviderFailure.RateLimited or DialogProviderFailure.Timeout or DialogProviderFailure.Network =>
            "provider-unavailable",
        DialogProviderFailure.Limit => "speech-content-limit",
        DialogProviderFailure.InvalidAudio or DialogProviderFailure.Protocol or DialogProviderFailure.InvalidRequest =>
            "provider-invalid-response",
        _ => "playback-failed"
    };

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Invalidate("disposed");
    }
}
