using OpenClaw.Shared.Speech;
using OpenClawTray.Chat;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ChatSpeechAttemptOwnerTests
{
    private static SpeechAttemptRequest Request(bool automatic = true, string incarnation = "physical-1") =>
        new("gateway", 1, new SpeechRenditionIdentity("agent:main", incarnation, "response", "rendition"), automatic);

    [Fact]
    public async Task PlaybackProgressCannotRegressOrOverwriteInvalidatedAttempt()
    {
        var entered = new TaskCompletionSource<SpeechPlaybackRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = Ready(async (work, _) => { entered.SetResult(work); await finish.Task; });
        var execution = owner.PlayPreparedAsync(Request(), "hello");
        var work = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("synthesizing", owner.Status.State);
        work.ReportPhase!(PcmPlaybackPhase.Buffering);
        Assert.Equal("buffering", owner.Status.State);
        work.ReportPhase(PcmPlaybackPhase.Playing);
        work.ReportPhase(PcmPlaybackPhase.Buffering);
        Assert.Equal("playing", owner.Status.State);
        owner.Invalidate("session-reset");
        work.ReportPhase(PcmPlaybackPhase.Playing);
        Assert.Equal("session-reset", owner.Status.State);
        Assert.True(owner.Status.PlaybackStarted);
        finish.SetResult();
        Assert.Equal(SpeechAttemptOutcome.Cancelled, (await execution).Outcome);
    }

    [Fact]
    public async Task CapabilityAndForegroundMustBeEstablishedBeforeSynthesis()
    {
        var calls = 0;
        using var owner = new ChatSpeechAttemptOwner((_, _) => { calls++; return Task.CompletedTask; },
            () => new Release(() => { }), new());
        Assert.Equal(SpeechAttemptOutcome.Skipped, (await owner.PlayPreparedAsync(Request(), "hello")).Outcome);
        owner.SetAvailable(true);
        Assert.Equal(SpeechAttemptOutcome.Skipped, (await owner.PlayPreparedAsync(Request(), "hello")).Outcome);
        owner.SetForeground(Request().Session);
        Assert.Equal(SpeechAttemptOutcome.Completed, (await owner.PlayPreparedAsync(Request(), "hello")).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LiveFinalIsNotReplayedButManualReplayIsNewAttemptEvenWhenMuted()
    {
        var texts = new List<string>();
        using var owner = Ready(async (work, token) =>
        {
            if (work.LiveText is { } live)
                await foreach (var text in live.WithCancellation(token)) texts.Add(text);
            else texts.Add(work.PreparedText!);
        });
        static async IAsyncEnumerable<string> Stream()
        {
            yield return "[curious] Hello";
            await Task.CompletedTask;
        }
        Assert.Equal(SpeechAttemptOutcome.Completed, (await owner.PlayLiveAsync(Request(), Stream())).Outcome);
        Assert.Equal(SpeechAttemptOutcome.Skipped, (await owner.PlayPreparedAsync(Request(), "[curious] Hello")).Outcome);
        owner.SetAutomaticMuted(true);
        Assert.Equal(SpeechAttemptOutcome.Completed, (await owner.PlayPreparedAsync(Request(false), "[curious] Hello")).Outcome);
        Assert.Equal(new[] { "[curious] Hello", "[curious] Hello" }, texts);
    }

    [Fact]
    public async Task SameKeyResetCancelsPendingWorkAndLateCompletionCannotReplaceNewStatus()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = 0;
        var releasedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new ChatSpeechAttemptOwner(async (_, _) => { entered.SetResult(); await delayed.Task; },
            () => new Release(() => { Interlocked.Increment(ref released); releasedSignal.TrySetResult(); }), new());
        owner.SetAvailable(true);
        owner.SetForeground(Request().Session);
        var running = owner.PlayPreparedAsync(Request(), "old");
        await entered.Task;
        owner.Invalidate("reset", Request().Session);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        owner.SetForeground(Request(incarnation: "physical-2").Session);
        delayed.SetResult();
        await releasedSignal.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(SpeechAttemptOutcome.Cancelled, result.Outcome);
        Assert.Equal("session-changed", owner.Status.State);
        Assert.Equal(1, released);
        Assert.Equal(SpeechAttemptOutcome.Skipped, (await owner.PlayPreparedAsync(Request(), "stale")).Outcome);
    }

    [Fact]
    public async Task NodeLeaseBlocksChatBeforeSynthesisAndChatStopCannotCancelNode()
    {
        var arbiter = new SpeechPlaybackArbiter();
        using var node = await arbiter.AcquireAsync(SpeechCaller.Node, true, default);
        var calls = 0;
        using var owner = Ready((_, _) => { calls++; return Task.CompletedTask; }, arbiter);
        Assert.Equal(SpeechAttemptOutcome.Failed, (await owner.PlayPreparedAsync(Request(), "hello")).Outcome);
        owner.Invalidate("stopped");
        arbiter.Stop(SpeechCaller.Chat);
        Assert.False(node.Token.IsCancellationRequested);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ProviderFailureAfterPlaybackUsesSafeCategoryAndRecordsPartialAudio()
    {
        using var owner = Ready((work, _) =>
        {
            work.ReportPhase!(PcmPlaybackPhase.Playing);
            throw new DialogProviderException(DialogProviderFailure.Authentication, "untrusted provider body");
        });

        var result = await owner.PlayPreparedAsync(Request(), "hello");

        Assert.Equal(SpeechAttemptOutcome.Failed, result.Outcome);
        Assert.Equal("provider-authentication", result.Reason);
        Assert.Equal("failed", owner.Status.State);
        Assert.Equal("provider-authentication", owner.Status.Reason);
        Assert.True(owner.Status.PlaybackStarted);
    }

    [Fact]
    public async Task SupersessionWaitsForReleaseAndUnrelatedCallersCannotStealLease()
    {
        var arbiter = new SpeechPlaybackArbiter();
        using var first = await arbiter.AcquireAsync(SpeechCaller.Chat, true, default);
        var waiting = arbiter.AcquireAsync(SpeechCaller.Chat, true, default);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var second = await waiting;
        first.Dispose();
        await Assert.ThrowsAsync<SpeechPlaybackBusyException>(() => arbiter.AcquireAsync(SpeechCaller.Node, true, default));
        Assert.False(second.Token.IsCancellationRequested);
        arbiter.Stop(SpeechCaller.Chat);
        Assert.True(second.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelledAttemptRetainsOutputLeaseUntilRealTeardownBeforeNewSynthesis()
    {
        var arbiter = new SpeechPlaybackArbiter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var owner = Ready(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await teardown.Task;
            }
            else secondEntered.SetResult();
        }, arbiter);
        var first = owner.PlayPreparedAsync(Request(false), "first");
        await entered.Task;
        owner.Invalidate("stopped");
        Assert.Equal(SpeechAttemptOutcome.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(1))).Outcome);
        var second = owner.PlayPreparedAsync(Request(false), "second");
        Assert.False(secondEntered.Task.IsCompleted);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<SpeechPlaybackBusyException>(() => arbiter.AcquireAsync(SpeechCaller.Node, true, default));
        teardown.SetResult();
        Assert.Equal(SpeechAttemptOutcome.Completed, (await second.WaitAsync(TimeSpan.FromSeconds(1))).Outcome);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OccupiedLeaseWaitCanBeCancelledWithoutStartingNewSynthesis()
    {
        var arbiter = new SpeechPlaybackArbiter();
        using var existing = await arbiter.AcquireAsync(SpeechCaller.Chat, true, default);
        using var cancel = new CancellationTokenSource();
        var waiting = arbiter.AcquireAsync(SpeechCaller.Chat, true, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAsync<SpeechPlaybackBusyException>(() => arbiter.AcquireAsync(SpeechCaller.Preview, true, default));
    }

    [Fact]
    public async Task AutomaticMuteDoesNotCancelExplicitManualPlayback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = Ready(async (_, token) => { entered.SetResult(); await finish.Task.WaitAsync(token); });
        var running = owner.PlayPreparedAsync(Request(false), "manual");
        await entered.Task;
        owner.SetAutomaticMuted(true);
        finish.SetResult();
        Assert.Equal(SpeechAttemptOutcome.Completed, (await running).Outcome);
    }

    [Fact]
    public async Task ReturningToSessionDoesNotAutomaticallyReplayConsumedRendition()
    {
        var calls = 0;
        using var owner = Ready((_, _) => { calls++; return Task.CompletedTask; });
        await owner.PlayPreparedAsync(Request(), "once");
        owner.SetForeground(null);
        owner.SetForeground(Request().Session);
        Assert.Equal(SpeechAttemptOutcome.Skipped, (await owner.PlayPreparedAsync(Request(), "once")).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AvailabilityAndForegroundChangesPublishTheReplacementStatus()
    {
        using var owner = Ready((_, _) => Task.CompletedTask);
        var states = new List<string>();
        owner.StatusChanged += (_, _) => states.Add(owner.Status.State);
        owner.SetAvailable(false);
        owner.SetAvailable(true);
        owner.SetForeground(Request(incarnation: "physical-2").Session);
        Assert.Equal(new[] { "unavailable", "session-changed" }, states);
        Assert.Null(owner.Status.EffectiveMode);
        Assert.Null(owner.Status.Reason);
    }

    private static ChatSpeechAttemptOwner Ready(Func<SpeechPlaybackRequest, CancellationToken, Task> play,
        SpeechPlaybackArbiter? arbiter = null)
    {
        var owner = new ChatSpeechAttemptOwner(play, () => new Release(() => { }), arbiter ?? new());
        owner.SetAvailable(true);
        owner.SetForeground(Request().Session);
        return owner;
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
