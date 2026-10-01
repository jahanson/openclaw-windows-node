using System.Text.RegularExpressions;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Speech;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Owns native chat provider lifecycle and chat-specific speech playback.
/// </summary>
public sealed class OpenClawChatCoordinator : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly Func<NodeService?> _nodeServiceAccessor;
    private readonly IOpenClawLogger _logger;
    private readonly Action<Action>? _post;
    private readonly object _gate = new();
    private readonly object _manualSpeechGate = new();
    private OpenClawChatDataProvider? _provider;
    private TextToSpeechService? _fallbackTextToSpeech;
    private string? _lastManualSpeechText;
    private DateTimeOffset _lastManualSpeechAt;
    private readonly object _microphoneGate = new();
    private readonly Dictionary<VoiceService, int> _microphoneMutes = [];
    private bool _disposed;
    private readonly ElevenLabsDialogClient _dialogClient = new();
    private readonly WasapiPcmPlayback _dialogPlayback = new();
    public ChatSpeechAttemptOwner DialogSpeech { get; }
    private ChatSpeechDelivery? _speechDelivery;
    private string _speechGatewayId = "";
    private string? _selectedSpeechSession;
    public event EventHandler? SpeechStatusChanged
    {
        add => DialogSpeech.StatusChanged += value;
        remove => DialogSpeech.StatusChanged -= value;
    }

    /// <summary>
    /// When true, all TTS playback (manual Read Aloud and auto-response speech) is suppressed.
    /// Toggled by the speaker mute button in the chat composer.
    /// Setting to true also interrupts any currently playing speech.
    /// </summary>
    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            DialogSpeech.SetAutomaticMuted(value);
            if (value && !DialogSpeech.IsManualPlaybackActive) _speechDelivery?.Invalidate(null, "muted");
            if (value && !DialogSpeech.IsManualPlaybackActive)
            {
                // Stop any currently playing speech immediately
                try { SpeechPlaybackArbiter.Shared.Stop(SpeechCaller.Chat); }
                catch (Exception ex) { _logger.Debug($"OpenClawChatCoordinator: StopSpeaking during mute failed: {ex.Message}"); }
            }
        }
    }

    public OpenClawChatCoordinator(
        SettingsManager settings,
        Func<NodeService?> nodeServiceAccessor,
        IOpenClawLogger logger,
        Action<Action>? post)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _nodeServiceAccessor = nodeServiceAccessor ?? throw new ArgumentNullException(nameof(nodeServiceAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _post = post;
        DialogSpeech = new ChatSpeechAttemptOwner(PlayDialogAsync, AcquireMicrophoneMute, SpeechPlaybackArbiter.Shared);
    }

    public OpenClawChatDataProvider? Provider
    {
        get
        {
            lock (_gate)
            {
                return _provider;
            }
        }
    }

    public void SetOperatorClient(OpenClawGatewayClient? client, string? gatewayId = null)
    {
        DialogSpeech.SetAvailable(false);
        DialogSpeech.SetForeground(null);
        OpenClawChatDataProvider? oldProvider;

        lock (_gate)
        {
            if (_disposed) return;
            oldProvider = _provider;
            _provider = null;
            _speechDelivery = null;
        }

        oldProvider?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (client is null)
        {
            return;
        }

        var newProvider = new OpenClawChatDataProvider(new GatewayClientChatBridge(client), _post);
        _speechGatewayId = gatewayId ?? Guid.NewGuid().ToString("N");
        var boundGatewayId = _speechGatewayId;
        var speech = new ChatSpeechDelivery(() => new SpeechGatewayTransport(client),
            key => newProvider.ResolveSpeechSession(boundGatewayId, key),
            () => _settings.ChatSpeechProvider == "elevenlabs-dialog" && SpeechSetupReadiness.IsAutomaticChatTtsEnabled(_settings) && !IsMuted &&
                !string.IsNullOrWhiteSpace(_settings.TtsElevenLabsApiKey) && !string.IsNullOrWhiteSpace(_settings.TtsElevenLabsVoiceId),
            () => _settings.ChatSpeechMode, DialogSpeech, newProvider.ReauthorizeSpeechAsync, newProvider.CaptureSpeechHistoryRefresh,
            () => _settings.NodeTtsEnabled && _settings.ChatSpeechProvider == "elevenlabs-dialog");
        newProvider.AttachSpeechDelivery(speech);
        speech.SelectForeground(_selectedSpeechSession);
        lock (_gate)
        {
            if (_disposed)
            {
                newProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return;
            }

            _provider = newProvider;
            _speechDelivery = speech;
        }
    }

    public Task SpeakChatTextAsync(string text)
    {
        if (ShouldSuppressDuplicateManualSpeech(text))
        {
            return Task.CompletedTask;
        }

        // Manual "play" button — bypass mute (mute is for auto-read only)
        return SpeakConfiguredTextAsync(text, muteVoiceCapture: true, bypassMute: true);
    }

    /// <summary>Stops any currently playing TTS audio immediately.</summary>
    public void StopSpeaking()
    {
        _speechDelivery?.Invalidate(null, "stopped");
        DialogSpeech.Invalidate("stopped");
        try { SpeechPlaybackArbiter.Shared.Stop(SpeechCaller.Chat); }
        catch (Exception ex) { _logger.Debug($"OpenClawChatCoordinator.StopSpeaking failed: {ex.Message}"); }
    }

    public Task SpeakResponseAsync(string text) => SpeakConfiguredTextAsync(text, muteVoiceCapture: true, bypassMute: false);

    public void SelectSpeechSession(string? sessionKey)
    {
        _selectedSpeechSession = sessionKey;
        _speechDelivery?.SelectForeground(sessionKey);
    }

    public Task<SpeechAttemptResult> ReplayRenditionAsync(SpeechRendition rendition, CancellationToken cancellationToken = default) =>
        _settings.NodeTtsEnabled && _settings.ChatSpeechProvider == "elevenlabs-dialog" && _speechDelivery is { } speech
            ? speech.ReplayAsync(rendition, cancellationToken)
            : Task.FromResult(new SpeechAttemptResult(SpeechAttemptOutcome.Skipped, "ElevenLabs Dialog is not enabled."));

    public async Task<SpeechAttemptResult> ReadWrittenAnswerAsync(string sessionKey, string entryId, string text,
        CancellationToken cancellationToken = default)
    {
        var provider = _provider;
        var delivery = _speechDelivery;
        if (!_settings.NodeTtsEnabled || _settings.ChatSpeechProvider != "elevenlabs-dialog" || provider is null || delivery is null)
            return new(SpeechAttemptOutcome.Skipped, "ElevenLabs Dialog is not enabled.");
        var authorizedText = await provider.ReauthorizeWrittenSpeechAsync(sessionKey, entryId, cancellationToken).ConfigureAwait(false);
        var identity = provider.ResolveSpeechSession(_speechGatewayId, sessionKey);
        if (identity is null || authorizedText is null || !ReferenceEquals(provider, _provider))
            return new(SpeechAttemptOutcome.Skipped, "This session is unavailable or the gateway did not authorize the answer.");
        var rendition = new SpeechRendition(new(sessionKey, identity.SessionIncarnation,
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")), entryId,
            SanitizeForSpeech(authorizedText), "written-fallback", "elevenlabs-audio-tags");
        return await delivery.PlayAuthorizedWrittenAsync(rendition, cancellationToken).ConfigureAwait(false);
    }

    public void RefreshSpeechSettings()
    {
        if (!_settings.NodeTtsEnabled || _settings.ChatSpeechProvider != "elevenlabs-dialog")
        {
            _speechDelivery?.Invalidate(null, "disabled");
            DialogSpeech.SetAvailable(false);
        }
        else if (!_settings.VoiceTtsEnabled && !DialogSpeech.IsManualPlaybackActive)
            _speechDelivery?.Invalidate(null, "automatic-speech-disabled");
    }

    private async Task SpeakConfiguredTextAsync(string text, bool muteVoiceCapture, bool bypassMute = false)
    {
        if (!bypassMute && IsMuted) return;
        using var microphone = muteVoiceCapture ? AcquireMicrophoneMute() : null;

        try
        {
            var speakText = SanitizeForSpeech(text);
            if (string.IsNullOrWhiteSpace(speakText)) return;
            var speakArgs = new TtsSpeakArgs
            {
                Text = speakText,
                // Leave provider unset so TextToSpeechService treats this as
                // configured/default playback and can fall back when needed.
                Provider = null,
                Interrupt = true
            };

            var ttsService = _nodeServiceAccessor()?.TextToSpeech
                ?? GetFallbackTextToSpeechService();
            await ttsService.SpeakAsync(speakArgs, SpeechCaller.Chat).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"TTS response playback failed: {ex.Message}");
        }
    }

    private TextToSpeechService GetFallbackTextToSpeechService()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _fallbackTextToSpeech ??= new TextToSpeechService(_logger, _settings);
        }
    }

    private async Task PlayDialogAsync(SpeechPlaybackRequest work, CancellationToken cancellationToken)
    {
        var request = new ElevenLabsDialogRequest
        {
            ApiKey = _settings.TtsElevenLabsApiKey ?? "",
            VoiceId = _settings.TtsElevenLabsVoiceId ?? "",
            CueFormat = work.Attempt.CueFormat
        };
        if (work.Attempt.Origin == "written-fallback"
            && work.PreparedText is { } written && WrittenAnswerSpeechPlayback.RequiresStreaming(written))
        {
            await new WrittenAnswerSpeechPlayback(_dialogClient, _dialogPlayback)
                .PlayAsync(request, written, cancellationToken, work.ReportPhase).ConfigureAwait(false);
        }
        else if (work.LiveText is { } live)
        {
            await _dialogPlayback.PlayAsync(ElevenLabsDialogClient.AudioFormat,
                (write, token) => _dialogClient.StreamAsync(request, live, write, token), cancellationToken, work.ReportPhase).ConfigureAwait(false);
        }
        else
        {
            var audio = await _dialogClient.GeneratePreparedAsync(request, work.PreparedText ?? "", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _dialogPlayback.PlayAsync(audio.Format,
                async (write, token) => await write(audio.AudioBytes, token).ConfigureAwait(false), cancellationToken, work.ReportPhase).ConfigureAwait(false);
        }
    }

    private IDisposable AcquireMicrophoneMute()
    {
        var service = _nodeServiceAccessor()?.VoiceService;
        if (service is not null)
        {
            lock (_microphoneGate)
            {
                _microphoneMutes.TryGetValue(service, out var count);
                _microphoneMutes[service] = count + 1;
                service.IsMutedForPlayback = true;
            }
        }
        return new SpeechMicrophoneLease(async () =>
        {
            if (service is null) return;
            await Task.Delay(300).ConfigureAwait(false);
            lock (_microphoneGate)
            {
                if (_microphoneMutes.TryGetValue(service, out var count) && count > 1)
                    _microphoneMutes[service] = count - 1;
                else
                {
                    _microphoneMutes.Remove(service);
                    service.IsMutedForPlayback = false;
                }
            }
        });
    }

    private bool ShouldSuppressDuplicateManualSpeech(string text)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_manualSpeechGate)
        {
            if (string.Equals(_lastManualSpeechText, text, StringComparison.Ordinal)
                && now - _lastManualSpeechAt < TimeSpan.FromSeconds(1))
            {
                return true;
            }

            _lastManualSpeechText = text;
            _lastManualSpeechAt = now;
            return false;
        }
    }

    /// <summary>
    /// Strips markdown formatting, emojis, code blocks, and other non-speakable
    /// content so TTS output sounds natural.
    /// </summary>
    private static string SanitizeForSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var s = text;

        // Remove fenced code blocks entirely (```...```)
        s = Regex.Replace(s, @"```[\s\S]*?```", " ");
        // Remove inline code (`...`)
        s = Regex.Replace(s, @"`[^`]+`", " ");
        // Remove markdown bold/italic markers (**, *, __, _)
        s = Regex.Replace(s, @"\*{1,2}(.+?)\*{1,2}", "$1");
        s = Regex.Replace(s, @"_{1,2}(.+?)_{1,2}", "$1");
        // Remove markdown headers (# ## ### etc.)
        s = Regex.Replace(s, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        // Remove markdown links [text](url) → text
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        // Remove raw URLs
        s = Regex.Replace(s, @"https?://\S+", " ");
        // Remove bullet/list markers
        s = Regex.Replace(s, @"^\s*[-*•]\s+", "", RegexOptions.Multiline);
        // Remove numbered list markers
        s = Regex.Replace(s, @"^\s*\d+\.\s+", "", RegexOptions.Multiline);
        // Remove emojis (supplementary plane: emoticons, symbols, etc.)
        s = Regex.Replace(s, @"[\u200B\uFE0F]", "");
        s = Regex.Replace(s, @"\p{Cs}{2}", " "); // surrogate pairs (emojis in supplementary planes)
        // Remove remaining special characters that sound odd when spoken
        s = Regex.Replace(s, @"[~|<>{}[\]\\*]", " ");
        // Collapse multiple spaces/newlines
        s = Regex.Replace(s, @"\s{2,}", " ");

        return s.Trim();
    }

    public void Dispose()
    {
        DialogSpeech.Dispose();
        _dialogClient.Dispose();
        OpenClawChatDataProvider? provider;
        TextToSpeechService? fallbackTextToSpeech;

        lock (_gate)
        {
            provider = _provider;
            fallbackTextToSpeech = _fallbackTextToSpeech;
            _provider = null;
            _fallbackTextToSpeech = null;
            _disposed = true;
        }

        provider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        fallbackTextToSpeech?.Dispose();
    }

    private sealed class SpeechMicrophoneLease(Func<Task> release) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _ = release();
        }
    }
}
