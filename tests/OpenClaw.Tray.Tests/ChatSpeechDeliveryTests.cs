using System.Threading.Channels;
using OpenClaw.Shared.Speech;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ChatSpeechDeliveryTests
{
    private static readonly SpeechRenditionIdentity Identity = new("agent:main", "physical", "run", "rendition");
    private static SpeechSessionIdentity Session => new("gateway", 1, Identity.SessionKey, Identity.SessionIncarnation);
    private static SpeechDeliveryEvent Begin(SpeechRenditionIdentity? identity = null) =>
        new(new(1, 0), identity ?? Identity, 0, "begin", Origin: "composed", CueFormat: "elevenlabs-audio-tags");
    private static SpeechDeliveryEvent Segment => new(new(1, 0), Identity, 1, "segment", "[curious] Hello");
    private static SpeechDeliveryEvent End => new(new(1, 0), Identity, 2, "terminal", "[curious] Hello", "complete", MessageId: "actual-message");

    private static SpeechDeliveryEvent FailedEnd(string reason) =>
        new(new(1, 0), Identity, 2, "terminal", Outcome: "failed", Reason: reason);

    [Fact]
    public async Task RemoteResetCancelsTerminalPlaybackEvenWhenPhysicalSessionIdIsUnchanged()
    {
        using var transport = new Transport();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new ChatSpeechAttemptOwner(async (_, token) =>
        {
            playing.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
        }, () => new Scope(), new());
        using var delivery = Create(transport, owner);
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        await transport.Batches.Writer.WriteAsync(new([Begin(), Segment, End], "terminal", 25));
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        delivery.Invalidate(Identity.SessionKey, "remote-session-reset");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await transport.Detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("remote-session-reset", owner.Status.State);
    }

    [Fact]
    public async Task AcceptedRunAbortBeforeBeginDetachesWithoutSynthesis()
    {
        using var transport = new Transport();
        var calls = 0;
        using var owner = new ChatSpeechAttemptOwner((_, _) => { calls++; return Task.CompletedTask; }, () => new Scope(), new());
        using var delivery = Create(transport, owner);
        var generation = await delivery.BeforeSendAsync(Identity.SessionKey, "request");
        delivery.BindAcceptedRun("request", generation, "accepted-run");
        delivery.InvalidateTurn("accepted-run", "chat-abort");
        await transport.Detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StaleAcceptedRunBindingCannotCancelNewAdmissionWithSameRequestKey()
    {
        using var transport = new Transport();
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new ChatSpeechAttemptOwner((_, _) => { executed.TrySetResult(); return Task.CompletedTask; }, () => new Scope(), new());
        using var delivery = Create(transport, owner);
        var oldGeneration = await delivery.BeforeSendAsync(Identity.SessionKey, "request");
        delivery.InvalidateTurn("request", "cancel-old");
        await transport.Detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newGeneration = await delivery.BeforeSendAsync(Identity.SessionKey, "request");
        Assert.NotEqual(oldGeneration, newGeneration);
        delivery.BindAcceptedRun("request", oldGeneration, "old-accepted-run");
        delivery.InvalidateTurn("old-accepted-run", "stale-abort");
        await transport.Batches.Writer.WriteAsync(new([Begin(), Segment, End], "terminal", 25));
        await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PreparedRegistersExactTurnAndOnlySpeaksValidatedTerminal()
    {
        using var transport = new Transport();
        var executed = new TaskCompletionSource<SpeechPlaybackRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new ChatSpeechAttemptOwner((work, _) => { executed.TrySetResult(work); return Task.CompletedTask; }, () => new Scope(), new());
        var refreshCount = 0;
        using var delivery = new ChatSpeechDelivery(() => transport, _ => Session, () => true, () => "prepared", owner,
            captureHistoryRefresh: identity =>
            {
                Assert.Equal(Identity, identity);
                return _ => { refreshCount++; return Task.CompletedTask; };
            });
        delivery.ConnectionChanged(true);
        delivery.SelectForeground(Identity.SessionKey);
        await delivery.BeforeSendAsync(Identity.SessionKey, "exact-idempotency");
        Assert.Equal("exact-idempotency", transport.IdempotencyKey);
        await transport.Batches.Writer.WriteAsync(new([Begin(), Segment], "active", 25));
        Assert.False(executed.Task.IsCompleted);
        await transport.Batches.Writer.WriteAsync(new([End], "terminal", 25));
        var work = await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("[curious] Hello", work.PreparedText);
        Assert.Equal(Identity, work.Attempt.Identity);
        Assert.Equal(1, refreshCount);
        await transport.Detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LiveConsumesCommittedTextBeforeTerminalWithoutFinalReplay()
    {
        using var transport = new Transport();
        var firstText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var owner = new ChatSpeechAttemptOwner(async (work, token) =>
        {
            Interlocked.Increment(ref calls);
            await foreach (var text in work.LiveText!.WithCancellation(token)) firstText.TrySetResult(text);
        }, () => new Scope(), new());
        using var delivery = Create(transport, owner, mode: "live");
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        await transport.Batches.Writer.WriteAsync(new([Begin(), Segment], "active", 25));
        Assert.Equal("[curious] Hello", await firstText.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(transport.Detached.Task.IsCompleted);
        await transport.Batches.Writer.WriteAsync(new([End], "terminal", 25));
        await transport.Detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedLiveTerminalReportsSanitizedReasonAndPartialPlayback()
    {
        using var transport = new Transport();
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new ChatSpeechAttemptOwner(async (work, token) =>
        {
            await foreach (var _ in work.LiveText!.WithCancellation(token))
            {
                work.ReportPhase!(PcmPlaybackPhase.Playing);
                played.TrySetResult();
            }
        }, () => new Scope(), new());
        using var delivery = Create(transport, owner, mode: "live");
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        await transport.Batches.Writer.WriteAsync(new([Begin(), Segment], "active", 25));
        await played.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await transport.Batches.Writer.WriteAsync(new([FailedEnd("content-limit")], "terminal", 25));
        await transport.Detached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("failed", owner.Status.State);
        Assert.Equal("speech-content-limit", owner.Status.Reason);
        Assert.True(owner.Status.PlaybackStarted);
    }

    [Fact]
    public async Task AutoSelectsLiveWhenTheGatewayReportsLiveCapability()
    {
        using var transport = new Transport();
        using var owner = new ChatSpeechAttemptOwner((_, _) => Task.CompletedTask, () => new Scope(), new());
        using var delivery = Create(transport, owner);
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        Assert.True(transport.LiveRequested is true);
        Assert.Equal("live", owner.Status.EffectiveMode);
        Assert.Null(owner.Status.Reason);
    }

    [Fact]
    public async Task AutoFallsBackToPreparedAndPublishesTheNegotiatedReason()
    {
        using var transport = new Transport { Live = false };
        using var owner = new ChatSpeechAttemptOwner((_, _) => Task.CompletedTask, () => new Scope(), new());
        using var delivery = Create(transport, owner);
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        Assert.True(transport.LiveRequested is false);
        Assert.Equal("composing", owner.Status.State);
        Assert.Equal("prepared", owner.Status.EffectiveMode);
        Assert.Equal("live-unavailable", owner.Status.Reason);
    }

    [Fact]
    public async Task PluginCannotAssertItsOwnPhysicalSessionAuthority()
    {
        using var transport = new Transport();
        var calls = 0;
        using var owner = new ChatSpeechAttemptOwner((_, _) => { calls++; return Task.CompletedTask; }, () => new Scope(), new());
        using var delivery = Create(transport, owner);
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        await transport.Batches.Writer.WriteAsync(new([Begin(Identity with { SessionIncarnation = "other" })], "active", 25));
        await transport.Detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RemoteSameKeyReplacementCancelsLiveDeliveryAndReleasesTicket()
    {
        using var transport = new Transport();
        var current = Session;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new ChatSpeechAttemptOwner(async (work, token) =>
        {
            started.SetResult();
            try { await foreach (var _ in work.LiveText!.WithCancellation(token)) { } }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        }, () => new Scope(), new());
        using var delivery = new ChatSpeechDelivery(() => transport, _ => current, () => true, () => "live", owner);
        delivery.ConnectionChanged(true);
        delivery.SelectForeground(Identity.SessionKey);
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        await transport.Batches.Writer.WriteAsync(new([Begin(), Segment], "active", 25));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        current = current with { SessionIncarnation = "replacement" };
        delivery.RefreshForeground();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await transport.Detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual("completed", owner.Status.State);
    }

    [Fact]
    public async Task MissingCapabilityDoesNotRegisterIntentOrFailOrdinarySendAdmission()
    {
        using var transport = new Transport { Available = false };
        using var owner = new ChatSpeechAttemptOwner((_, _) => throw new Exception("Must not synthesize"), () => new Scope(), new());
        using var delivery = Create(transport, owner);
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        Assert.Null(transport.IdempotencyKey);
    }

    [Fact]
    public async Task CapabilityFailurePublishesAnActionableReadinessReason()
    {
        using var transport = new Transport { Available = false, Reason = "session-core-tts-must-be-off" };
        using var owner = new ChatSpeechAttemptOwner((_, _) => throw new Exception("Must not synthesize"), () => new Scope(), new());
        using var delivery = Create(transport, owner);
        await delivery.BeforeSendAsync(Identity.SessionKey, "turn");
        Assert.Equal("unavailable", owner.Status.State);
        Assert.Equal("session-core-tts-must-be-off", owner.Status.Reason);
        Assert.Equal(Identity.SessionKey, transport.CapabilitiesSessionKey);
        Assert.Null(transport.IdempotencyKey);
    }

    private static ChatSpeechDelivery Create(Transport transport, ChatSpeechAttemptOwner owner, string mode = "auto")
    {
        var delivery = new ChatSpeechDelivery(() => transport, _ => Session, () => true, () => mode, owner);
        delivery.ConnectionChanged(true);
        delivery.SelectForeground(Identity.SessionKey);
        return delivery;
    }

    [Fact]
    public async Task RevokingOutputPermissionDuringHistoryAuthorizationPreventsBilling()
    {
        using var transport = new Transport();
        var authorization = new TaskCompletionSource<SpeechRendition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = true;
        var calls = 0;
        using var owner = new ChatSpeechAttemptOwner((_, _) => { calls++; return Task.CompletedTask; }, () => new Scope(), new());
        using var delivery = new ChatSpeechDelivery(() => transport, _ => Session, () => false, () => "auto", owner,
            (_, _) => authorization.Task, manualEnabled: () => permission);
        delivery.ConnectionChanged(true);
        delivery.SelectForeground(Identity.SessionKey);
        var rendition = new SpeechRendition(Identity, "actual-message", "hello", "composed", "elevenlabs-audio-tags");
        var replay = delivery.ReplayAsync(rendition);
        permission = false;
        authorization.SetResult(rendition);
        Assert.Equal(SpeechAttemptOutcome.Skipped, (await replay).Outcome);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true, SpeechAttemptOutcome.Completed, 1)]
    [InlineData(false, SpeechAttemptOutcome.Skipped, 0)]
    public async Task ManualReplayRequiresFreshCoreAuthorizationButNotComposeCapability(bool authorized,
        SpeechAttemptOutcome expected, int expectedCalls)
    {
        using var transport = new Transport { Available = false };
        var calls = 0;
        var authorizationChecks = 0;
        using var owner = new ChatSpeechAttemptOwner((_, _) => { calls++; return Task.CompletedTask; }, () => new Scope(), new());
        using var delivery = new ChatSpeechDelivery(() => transport, _ => Session, () => false, () => "auto", owner,
            (rendition, _) =>
            {
                authorizationChecks++;
                return Task.FromResult(authorized ? rendition : null);
            });
        delivery.ConnectionChanged(true);
        delivery.SelectForeground(Identity.SessionKey);
        var result = await delivery.ReplayAsync(new(Identity, "actual-message", "[curious] Hello", "composed", "elevenlabs-audio-tags"));
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(1, authorizationChecks);
        Assert.Equal(expectedCalls, calls);
        Assert.Null(transport.IdempotencyKey);
    }

    [Theory]
    [InlineData(2731, "written-fallback", "live")]
    [InlineData(2000, "written-fallback", "prepared")]
    [InlineData(499, "composed", "prepared")]
    public async Task LongExplicitWrittenReadingUsesStreamingWithoutChangingComposedReplay(int length, string origin, string mode)
    {
        using var transport = new Transport();
        using var owner = new ChatSpeechAttemptOwner((_, _) => Task.CompletedTask, () => new Scope(), new());
        using var delivery = Create(transport, owner);
        var written = new SpeechRendition(Identity, "actual-message", new string('x', length),
            origin, "elevenlabs-audio-tags");
        Assert.Equal(SpeechAttemptOutcome.Completed, (await delivery.PlayAuthorizedWrittenAsync(written, CancellationToken.None)).Outcome);
        Assert.Equal(mode, owner.Status.EffectiveMode);
        Assert.Equal(origin, owner.Status.Attempt!.Origin);
    }

    private sealed class Scope : IDisposable { public void Dispose() { } }
    private sealed class Transport : ISpeechGatewayTransport
    {
        public bool Available { get; init; } = true;
        public bool Prepared { get; init; } = true;
        public bool Live { get; init; } = true;
        public string? Reason { get; init; }
        public string? IdempotencyKey { get; private set; }
        public bool? LiveRequested { get; private set; }
        public string? CapabilitiesSessionKey { get; private set; }
        public Channel<SpeechReadBatch> Batches { get; } = Channel.CreateUnbounded<SpeechReadBatch>();
        public TaskCompletionSource Detached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SpeechCapabilities> GetCapabilitiesAsync(string? sessionKey = null,
            CancellationToken cancellationToken = default)
        {
            CapabilitiesSessionKey = sessionKey;
            return Task.FromResult(new SpeechCapabilities(Available, Available && Prepared, Available && Live, Reason));
        }
        public Task<SpeechTurnTicket> RegisterIntentAsync(string sessionKey, string idempotencyKey, bool live, CancellationToken cancellationToken = default)
        { IdempotencyKey = idempotencyKey; LiveRequested = live; return Task.FromResult(new SpeechTurnTicket("ticket", long.MaxValue)); }
        public Task<SpeechReadBatch> ReadAsync(SpeechTurnTicket ticket, int afterSequence, CancellationToken cancellationToken = default) =>
            Batches.Reader.ReadAsync(cancellationToken).AsTask();
        public Task DetachAsync(SpeechTurnTicket ticket, CancellationToken cancellationToken = default)
        { Detached.TrySetResult(); return Task.CompletedTask; }
        public void Dispose() { }
    }
}
