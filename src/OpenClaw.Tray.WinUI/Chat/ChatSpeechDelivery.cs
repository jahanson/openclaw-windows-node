using System.Threading.Channels;
using OpenClaw.Shared.Speech;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Binds one foreground turn's authorized plugin delivery to local playback. The conversation
/// state supplies physical identity; plugin event data never establishes session authority.
/// </summary>
public sealed class ChatSpeechDelivery : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<ISpeechGatewayTransport> _transportFactory;
    private readonly Func<string, SpeechSessionIdentity?> _resolveSession;
    private readonly Func<bool> _automaticEnabled;
    private readonly Func<string> _mode;
    private readonly ChatSpeechAttemptOwner _playback;
    private readonly Func<SpeechRendition, CancellationToken, Task<SpeechRendition?>>? _authorizeReplay;
    private readonly Func<SpeechRenditionIdentity, Func<CancellationToken, Task>?>? _captureHistoryRefresh;
    private readonly Func<bool> _manualEnabled;
    private ISpeechGatewayTransport? _transport;
    private string? _foregroundKey;
    private SpeechSessionIdentity? _foreground;
    private CancellationTokenSource? _active;
    private string? _activeTurnId;
    private string? _activeResponseId;
    private string? _activeAcceptedRunId;
    private long _generation;
    private bool _disposed;

    public ChatSpeechDelivery(Func<ISpeechGatewayTransport> transportFactory,
        Func<string, SpeechSessionIdentity?> resolveSession, Func<bool> automaticEnabled,
        Func<string> mode, ChatSpeechAttemptOwner playback,
        Func<SpeechRendition, CancellationToken, Task<SpeechRendition?>>? authorizeReplay = null,
        Func<SpeechRenditionIdentity, Func<CancellationToken, Task>?>? captureHistoryRefresh = null,
        Func<bool>? manualEnabled = null)
    {
        _transportFactory = transportFactory;
        _resolveSession = resolveSession;
        _automaticEnabled = automaticEnabled;
        _mode = mode;
        _playback = playback;
        _authorizeReplay = authorizeReplay;
        _captureHistoryRefresh = captureHistoryRefresh;
        _manualEnabled = manualEnabled ?? (() => true);
    }

    public void ConnectionChanged(bool ready)
    {
        Invalidate(null, "connection-changed");
        lock (_gate)
        {
            _transport?.Dispose();
            _transport = !_disposed && ready ? _transportFactory() : null;
        }
        _playback.SetAvailable(false);
        RefreshForeground();
    }

    public void SelectForeground(string? sessionKey)
    {
        lock (_gate) _foregroundKey = sessionKey;
        RefreshForeground();
    }

    public void RefreshForeground()
    {
        CancellationTokenSource? old = null;
        SpeechSessionIdentity? current;
        lock (_gate)
        {
            current = _foregroundKey is { } key ? _resolveSession(key) : null;
            if (current != _foreground)
            {
                _foreground = current;
                ++_generation;
                old = _active;
                _active = null;
            }
            _playback.SetForeground(current);
        }
        Cancel(old);
    }

    public void Invalidate(string? sessionKey, string reason)
    {
        CancellationTokenSource? old;
        lock (_gate)
        {
            if (sessionKey is not null && sessionKey != _foregroundKey) return;
            ++_generation;
            old = _active;
            _active = null;
            _playback.Invalidate(reason);
        }
        Cancel(old);
    }

    public void InvalidateTurn(string turnId, string reason)
    {
        lock (_gate)
        {
            if (_active is null || (_activeTurnId != turnId && _activeResponseId != turnId && _activeAcceptedRunId != turnId)) return;
            Invalidate(null, reason);
        }
    }

    public void BindAcceptedRun(string idempotencyKey, long? generation, string acceptedRunId)
    {
        lock (_gate)
        {
            if (_active is not null && generation == _generation && _activeTurnId == idempotencyKey)
                _activeAcceptedRunId = acceptedRunId;
        }
    }

    /// <summary>Called before ordinary chat.send with its exact idempotency key. Speech failure never rejects chat.</summary>
    public async Task<long?> BeforeSendAsync(string sessionKey, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (!_automaticEnabled()) return null;
        ISpeechGatewayTransport? transport;
        CancellationTokenSource lifetime;
        CancellationTokenSource? previous;
        long generation;
        lock (_gate)
        {
            if (_disposed || _transport is null || sessionKey != _foregroundKey) return null;
            transport = _transport;
            previous = _active;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(TimeSpan.FromSeconds(180));
            _active = lifetime;
            _activeTurnId = idempotencyKey;
            _activeResponseId = null;
            _activeAcceptedRunId = null;
            generation = ++_generation;
        }
        Cancel(previous);
        try
        {
            var capability = await transport.GetCapabilitiesAsync(sessionKey, lifetime.Token).ConfigureAwait(false);
            EnsureCurrent();
            var requestedMode = SettingsManager.NormalizeChatSpeechMode(_mode());
            var live = requestedMode == SettingsManager.ChatSpeechModeLive ||
                requestedMode == SettingsManager.ChatSpeechModeAuto && capability.Live;
            var modeAvailable = live ? capability.Live : capability.Prepared;
            if (!capability.Available || !modeAvailable)
            {
                lock (_gate)
                {
                    if (_generation != generation) throw new OperationCanceledException(lifetime.Token);
                    _playback.SetUnavailable(capability.Reason switch
                    {
                        "signed-admin-device-required" => "signed-admin-device-required",
                        "plugin-disabled" => "plugin-disabled",
                        "unsupported-host" => "unsupported-host",
                        "incompatible-version" => "incompatible-version",
                        "session-core-tts-must-be-off" => "session-core-tts-must-be-off",
                        "unsupported-model" => "unsupported-model",
                        "invalid-session" => "invalid-session",
                        _ => "mode-unavailable"
                    });
                }
                FinishAdmission();
                return null;
            }
            EnsureCurrent();
            _playback.SetComposing(live ? SettingsManager.ChatSpeechModeLive : SettingsManager.ChatSpeechModePrepared,
                requestedMode == SettingsManager.ChatSpeechModeAuto && !live ? "live-unavailable" : null);
            var ticket = await transport.RegisterIntentAsync(sessionKey, idempotencyKey, live, lifetime.Token).ConfigureAwait(false);
            // Polling owns cleanup after admission; it does not delay the ordinary chat.send.
            _ = ReceiveAsync(transport, ticket, sessionKey, live, generation, lifetime);
            return generation;
        }
        catch (Exception)
        {
            FinishAdmission();
            lock (_gate)
                if (_generation == generation) _playback.SetUnavailable("intent-unavailable");
            return null;
        }

        void EnsureCurrent()
        {
            lifetime.Token.ThrowIfCancellationRequested();
            lock (_gate) if (_generation != generation) throw new OperationCanceledException(lifetime.Token);
        }
        void FinishAdmission()
        {
            lock (_gate) if (ReferenceEquals(_active, lifetime)) _active = null;
            lifetime.Dispose();
        }
    }

    private async Task ReceiveAsync(ISpeechGatewayTransport transport, SpeechTurnTicket ticket,
        string sessionKey, bool live, long generation, CancellationTokenSource lifetime)
    {
        Channel<string>? channel = null;
        Task<SpeechAttemptResult>? playing = null;
        try
        {
            SpeechRenditionAssembler? assembler = null;
            SpeechAttemptRequest? request = null;
            Func<CancellationToken, Task>? refreshHistory = null;
            var sequence = -1;
            while (true)
            {
                var batch = await transport.ReadAsync(ticket, sequence, lifetime.Token).ConfigureAwait(false);
                foreach (var item in batch.Events)
                {
                    SpeechSessionIdentity? canonical;
                    lock (_gate)
                    {
                        if (_generation != generation || _foregroundKey != sessionKey) throw new OperationCanceledException(lifetime.Token);
                        canonical = _resolveSession(sessionKey);
                    }
                    if (canonical is null || canonical.SessionIncarnation != item.Identity.SessionIncarnation ||
                        item.Identity.SessionKey != sessionKey || request is not null && request.Session != canonical)
                        throw new SpeechProtocolException("Speech session identity is not ready or has changed.");
                    if (assembler is null)
                    {
                        lock (_gate)
                        {
                            if (_generation != generation) throw new OperationCanceledException(lifetime.Token);
                            _foreground = canonical;
                            _activeResponseId = item.Identity.ResponseId;
                            _playback.SetForeground(canonical);
                        }
                        assembler = new(item.Identity);
                        refreshHistory = _captureHistoryRefresh?.Invoke(item.Identity);
                        request = new(canonical.GatewayId, canonical.ConnectionGeneration, item.Identity, true);
                    }
                    var change = assembler.Accept(item);
                    sequence = Math.Max(sequence, item.Sequence);
                    if (change == SpeechDeliveryChange.Appended && live)
                    {
                        if (channel is null)
                        {
                            channel = Channel.CreateBounded<string>(new BoundedChannelOptions(8)
                                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
                            lock (_gate)
                            {
                                if (_generation != generation) throw new OperationCanceledException(lifetime.Token);
                                playing = _playback.PlayLiveAsync(request!, channel.Reader.ReadAllAsync(lifetime.Token), lifetime.Token);
                            }
                        }
                        if (playing!.IsCompleted && (await playing.ConfigureAwait(false)).Outcome != SpeechAttemptOutcome.Completed)
                            throw new SpeechProtocolException("Speech playback stopped before delivery completed.");
                        await channel.Writer.WriteAsync(item.Text!, lifetime.Token).ConfigureAwait(false);
                    }
                    if (change != SpeechDeliveryChange.Terminal) continue;
                    if (!assembler.IsComplete)
                        throw TerminalFailure(assembler.Outcome, assembler.Reason);
                    if (refreshHistory is not null)
                    {
                        // Core history remains the only metadata authority. Its existing loader owns
                        // reset/connection fencing and publication; refresh failure must not stop audio.
                        try
                        {
                            _ = refreshHistory(lifetime.Token).ContinueWith(static task => _ = task.Exception,
                                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                        catch (Exception) { System.Diagnostics.Trace.TraceWarning("Speech history refresh could not be scheduled."); }
                    }
                    if (live)
                    {
                        channel?.Writer.TryComplete();
                        if (playing is not null) await playing.ConfigureAwait(false);
                    }
                    else
                    {
                        lock (_gate)
                        {
                            if (_generation != generation) throw new OperationCanceledException(lifetime.Token);
                            playing = _playback.PlayPreparedAsync(request!, assembler.Text, lifetime.Token);
                        }
                        await playing.ConfigureAwait(false);
                    }
                    return;
                }
                if (batch.State == "terminal") throw new SpeechProtocolException("Speech delivery ended without a terminal rendition.");
                await Task.Delay(batch.RetryAfterMs, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (SpeechDeliveryStatusException error)
        {
            lifetime.Cancel();
            channel?.Writer.TryComplete(new OperationCanceledException());
            lock (_gate)
                if (_generation == generation) _playback.SetTerminalStatus(error.State, error.Reason);
        }
        catch (Exception)
        {
            lifetime.Cancel();
            channel?.Writer.TryComplete(new OperationCanceledException());
            lock (_gate)
                if (_generation == generation) _playback.SetTerminalStatus("failed", "speech-generation-failed");
        }
        finally
        {
            if (playing is not null)
            {
                try { await playing.ConfigureAwait(false); } catch (Exception) { System.Diagnostics.Trace.TraceWarning("Speech playback cleanup observed a failure."); }
            }
            using var detach = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await transport.DetachAsync(ticket, detach.Token).ConfigureAwait(false); } catch (Exception) { System.Diagnostics.Trace.TraceWarning("Speech ticket detach did not complete."); }
            lock (_gate) if (ReferenceEquals(_active, lifetime)) _active = null;
            lifetime.Dispose();
        }
    }

    public async Task<SpeechAttemptResult> ReplayAsync(SpeechRendition rendition, CancellationToken token = default)
    {
        var authorized = _authorizeReplay is null ? null : await _authorizeReplay(rendition, token).ConfigureAwait(false);
        return authorized is null ? new(SpeechAttemptOutcome.Skipped, "The gateway could not authorize this stored speech.")
            : await PlayAuthorizedAsync(authorized, token).ConfigureAwait(false);
    }

    internal Task<SpeechAttemptResult> PlayAuthorizedWrittenAsync(SpeechRendition rendition, CancellationToken token) =>
        PlayAuthorizedAsync(rendition, token);

    private async Task<SpeechAttemptResult> PlayAuthorizedAsync(SpeechRendition rendition, CancellationToken token)
    {
        Task<SpeechAttemptResult> playback;
        lock (_gate)
        {
            var canonical = _foregroundKey == rendition.Identity.SessionKey ? _resolveSession(_foregroundKey) : null;
            if (_disposed || !_manualEnabled() || canonical is null || canonical.SessionIncarnation != rendition.Identity.SessionIncarnation || _transport is null)
                return new(SpeechAttemptOutcome.Skipped, "This speech rendition belongs to an unavailable session.");
            _playback.SetAvailable(true);
            _playback.SetForeground(canonical);
            playback = _playback.PlayPreparedAsync(new(canonical.GatewayId, canonical.ConnectionGeneration,
                rendition.Identity, false, rendition.CueFormat, rendition.Origin), rendition.Text, token);
        }
        return await playback.ConfigureAwait(false);
    }

    private static void Cancel(CancellationTokenSource? source)
    { try { source?.Cancel(); } catch (ObjectDisposedException) { } }

    private static SpeechDeliveryStatusException TerminalFailure(string? outcome, string? reason)
    {
        var state = outcome switch
        {
            "cancelled" => "cancelled",
            "unavailable" => "unavailable",
            _ => "failed"
        };
        var sanitized = reason switch
        {
            "missing-speech" or "empty-speech" or "early-final-phase-unavailable" =>
                "speech-content-unavailable",
            "content-limit" or "event-limit" or "speech-limit" or "written-limit" =>
                "speech-content-limit",
            "session-invalidated" or "run-cancelled" => "speech-session-invalidated",
            "unsupported-model" or "unsupported-runner" or "unsupported-session-policy" or
                "unsupported-session-overrides" or "session-core-tts-must-be-off" => "speech-eligibility-changed",
            "already-ended" or "content-incomplete" or "incomplete-marker" or "invalid-unicode" or
                "multiple-final-blocks" or "nested-speech" or "provider-revised-text" or "repeated-speech" or
                "tool-after-final-speech" or "unexpected-close" or "unfinished-cue" or "unclosed-speech" =>
                "speech-content-invalid",
            "model-error" or "provider-stream-failed" or "rendition-not-persisted" or "run-failed" =>
                "speech-generation-failed",
            _ => "speech-generation-failed"
        };
        return new(state, sanitized);
    }

    private sealed class SpeechDeliveryStatusException(string state, string reason) : Exception
    {
        public string State { get; } = state;
        public string Reason { get; } = reason;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        ConnectionChanged(false);
    }
}
