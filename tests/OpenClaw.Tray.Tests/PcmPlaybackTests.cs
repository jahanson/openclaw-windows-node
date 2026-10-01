using NAudio.Wave;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class PcmPlaybackTests
{
    [Fact]
    public async Task Buffer_PreservesSplitFramesAndTreatsStarvationAsSilenceUntilConfirmedEnd()
    {
        var buffer = new PcmPlaybackBuffer(new PcmAudioFormat(8000));
        await buffer.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        byte[] destination = [99, 99, 99, 99];
        Assert.Equal(4, buffer.Read(destination));
        Assert.Equal(new byte[] { 1, 2, 0, 0 }, destination);
        Assert.False(buffer.IsDrained);
        Assert.False(buffer.Ready.IsCompleted);
        await buffer.WriteAsync(new byte[] { 4 }, CancellationToken.None);
        buffer.Complete();
        await buffer.Ready;
        Assert.Equal(2, buffer.Read(destination));
        Assert.Equal(new byte[] { 3, 4 }, destination[..2]);
        Assert.Equal(0, buffer.Read(destination));
        Assert.True(buffer.IsDrained);
        Assert.Equal(1, buffer.Underruns);
    }

    [Fact]
    public async Task Buffer_BackpressureResumesAtLowWatermarkAndRetainsOrderAcrossWrap()
    {
        var format = new PcmAudioFormat(8000);
        var buffer = new PcmPlaybackBuffer(format);
        byte[] audio = Enumerable.Range(0, format.BytesPerSecond * 12).Select(i => (byte)(i % 251)).ToArray();
        Task writer = buffer.WriteAsync(audio, CancellationToken.None).AsTask();
        await buffer.Ready;
        Assert.False(writer.IsCompleted);
        Assert.Equal(format.BytesPerSecond * 5, buffer.BufferedBytes);
        var actual = new List<byte>();
        byte[] block = new byte[format.BytesPerSecond * 2];
        Assert.Equal(block.Length, buffer.Read(block));
        actual.AddRange(block);
        Assert.False(writer.IsCompleted);
        // Drain only real bytes, not underrun silence, as the bounded producer wakes.
        while (!writer.IsCompleted || buffer.BufferedBytes > 0)
        {
            int available = buffer.BufferedBytes;
            if (available == 0)
            {
                await Task.Yield();
                continue;
            }
            byte[] next = new byte[Math.Min(available, block.Length)];
            Assert.Equal(next.Length, buffer.Read(next));
            actual.AddRange(next);
            Assert.InRange(buffer.BufferedBytes, 0, format.BytesPerSecond * 5);
        }
        await writer;
        buffer.Complete();
        Assert.Equal(audio, actual);
        Assert.True(buffer.IsDrained);
    }

    [Fact]
    public async Task Buffer_AbortUnblocksProducerAndRejectsLateAudio()
    {
        var buffer = new PcmPlaybackBuffer(new PcmAudioFormat(8000));
        Task writer = buffer.WriteAsync(new byte[100000], CancellationToken.None).AsTask();
        await buffer.Ready;
        Assert.False(writer.IsCompleted);
        buffer.Abort();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, buffer.Read(new byte[20]));
        Assert.Equal(0, buffer.BufferedBytes);
        Assert.False(buffer.IsDrained);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buffer.WriteAsync(new byte[2], CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Buffer_RejectsEmptyOrTruncatedAudio(int byteCount)
    {
        var buffer = new PcmPlaybackBuffer(new PcmAudioFormat(24000));
        await buffer.WriteAsync(new byte[byteCount], CancellationToken.None);
        Assert.Throws<InvalidDataException>(buffer.Complete);
    }

    [Theory]
    [InlineData(8000, 2880001)] // 180-second decoded-duration cap is tighter.
    [InlineData(192000, 33554433)] // Encoded-byte cap is tighter.
    public async Task Buffer_RejectsOverBudgetBeforeEnqueuing(int rate, int length)
    {
        var buffer = new PcmPlaybackBuffer(new PcmAudioFormat(rate));
        await Assert.ThrowsAsync<InvalidDataException>(() => buffer.WriteAsync(new byte[length], CancellationToken.None).AsTask());
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public async Task Player_StartsBeforeProviderEndButCompletesOnlyAfterDeviceDrain()
    {
        var device = new ControlledOutput();
        var player = new WasapiPcmPlayback(() => device);
        var finishProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var play = player.PlayAsync(new PcmAudioFormat(8000), async (write, token) =>
        {
            await write(new byte[4000], token);
            await finishProvider.Task.WaitAsync(token);
        });
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(play.IsCompleted);
        Assert.Equal(8000, device.Provider!.WaveFormat.SampleRate);
        Assert.Equal(16, device.Provider.WaveFormat.BitsPerSample);
        Assert.Equal(1, device.Provider.WaveFormat.Channels);
        Assert.Equal(4000, device.Read(4000));
        Assert.Equal(2, device.Read(2)); // Underrun does not end playback.
        finishProvider.SetResult();
        Assert.Equal(2, device.Read(2)); // EOF cannot outrun the device clock either.
        device.AdvanceClockPastSubmittedAudio();
        await WaitForEofAsync(device);
        Assert.False(play.IsCompleted); // EOF queued is not hardware drain.
        device.SignalStopped();
        var result = await play.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4000, result.AudioBytes);
        Assert.True(device.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Player_DeviceFailureOrPrematureStopCancelsProductionAndDisposes(bool deviceError)
    {
        var device = new ControlledOutput();
        var player = new WasapiPcmPlayback(() => device);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var play = player.PlayAsync(new PcmAudioFormat(8000), async (write, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            await write(new byte[4000], token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        device.SignalStopped(deviceError ? new IOException("Endpoint removed") : null);
        await Assert.ThrowsAsync<IOException>(() => play.WaitAsync(TimeSpan.FromSeconds(2)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(device.Disposed);
        Assert.Equal(0, device.Read(2));
    }

    [Fact]
    public async Task Player_ProviderEofWaitsForHardwareClockBeforeEndingDeviceInput()
    {
        var device = new ControlledOutput();
        var player = new WasapiPcmPlayback(() => device);
        var play = player.PlayAsync(new PcmAudioFormat(8000), async (write, token) =>
            await write(new byte[4000], token));
        // Producer completes synchronously, before Play, including buffer.Complete.
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4000, device.Read(4000));
        Assert.Equal(2, device.Read(2));
        Assert.False(play.IsCompleted);
        device.AdvanceClockPastSubmittedAudio();
        Assert.Equal(0, device.Read(2));
        Assert.False(play.IsCompleted);
        device.SignalStopped();
        await play.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Player_StopDuringProviderWaitInvalidatesAudioWithoutWaitingForProvider(bool alreadyPlaying)
    {
        var device = new ControlledOutput();
        var player = new WasapiPcmPlayback(() => device);
        using var cancellation = new CancellationTokenSource();
        var uncooperative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedProducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var play = player.PlayAsync(new PcmAudioFormat(8000), async (write, token) =>
        {
            if (alreadyPlaying)
                await write(new byte[4000], token);
            startedProducer.SetResult();
            await uncooperative.Task;
            await write(new byte[4000], CancellationToken.None);
        }, cancellation.Token);
        await startedProducer.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (alreadyPlaying)
            await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(alreadyPlaying, device.Started.Task.IsCompleted);
        Assert.True(device.Disposed);
        Assert.Equal(0, device.Read(2));
        uncooperative.SetResult();
    }

    [Fact]
    public async Task Player_ProviderFailureAfterPlaybackStartsDiscardsRemainingAudio()
    {
        var device = new ControlledOutput();
        var player = new WasapiPcmPlayback(() => device);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var play = player.PlayAsync(new PcmAudioFormat(8000), async (write, token) =>
        {
            await write(new byte[4000], token);
            await fail.Task.WaitAsync(token);
            throw new InvalidDataException("Provider audio stream failed.");
        });
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, device.Read(2));
        fail.SetResult();
        await Assert.ThrowsAsync<InvalidDataException>(() => play.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, device.Read(2));
        Assert.True(device.Disposed);
    }

    [Theory]
    [InlineData(24000, 1, 16, "mp3", "audio/pcm")]
    [InlineData(24000, 1, 16, "pcm_s16le", "audio/mpeg")]
    [InlineData(24000, 1, 32, "pcm_s16le", "audio/pcm")]
    public void Format_RejectsUnimplementedDecoding(int rate, int channels, int bits, string encoding, string contentType)
    {
        Assert.Throws<NotSupportedException>(() => new PcmAudioFormat(rate, channels, bits, encoding, contentType));
    }

    private static async Task WaitForEofAsync(ControlledOutput output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (output.Read(2) != 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    // Models only the hardware boundary: tests explicitly drive reads and the separate
    // stopped callback so application queue exhaustion cannot manufacture hardware drain.
    private sealed class ControlledOutput : IWavePlayer, IWavePosition
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IWaveProvider? Provider { get; private set; }
        public bool Disposed { get; private set; }
        private long _position;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; }
        public WaveFormat OutputWaveFormat => Provider!.WaveFormat;
        public void Init(IWaveProvider waveProvider) => Provider = waveProvider;
        public void Play() { PlaybackState = PlaybackState.Playing; Started.TrySetResult(); }
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Dispose() => Disposed = true;
        public long GetPosition() => _position;
        public void AdvanceClockPastSubmittedAudio() => _position = long.MaxValue;
        public int Read(int count) => Provider!.Read(new byte[count], 0, count);
        public void SignalStopped(Exception? error = null) => PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }
}
