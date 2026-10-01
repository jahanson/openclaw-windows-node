using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OpenClawTray.Services;

public enum PcmPlaybackPhase { Buffering, Playing }

public sealed record PcmPlaybackResult(long AudioBytes, int Underruns);

/// <summary>
/// Owns one output device for one attempt. Caller arbitration and microphone muting belong
/// to the speech coordinator. The producer must honor cancellation and await every write.
/// </summary>
public sealed class WasapiPcmPlayback
{
    private readonly Func<IWavePlayer> _createOutput;

    public WasapiPcmPlayback()
        : this(() => new WasapiOut(AudioClientShareMode.Shared, true, 50)) { }

    // Output construction also permits selecting a concrete user-selected endpoint at composition.
    public WasapiPcmPlayback(Func<IWavePlayer> createOutput)
    {
        ArgumentNullException.ThrowIfNull(createOutput);
        _createOutput = createOutput;
    }

    public Task<PcmPlaybackResult> PlayAsync(
        PcmAudioFormat format,
        Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task> produceAudio,
        CancellationToken cancellationToken = default,
        Action<PcmPlaybackPhase>? reportPhase = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(produceAudio);
        // WasapiOut captures SynchronizationContext at construction. Construct off the UI thread
        // so stopped/error delivery cannot depend on a blocked window dispatcher.
        return Task.Run(() => RunAsync(format, produceAudio, cancellationToken, reportPhase), cancellationToken);
    }

    private async Task<PcmPlaybackResult> RunAsync(
        PcmAudioFormat format,
        Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task> produceAudio,
        CancellationToken cancellationToken, Action<PcmPlaybackPhase>? reportPhase)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(180));
        var attemptToken = lifetime.Token;
        var buffer = new PcmPlaybackBuffer(format);
        using var invalidation = attemptToken.Register(buffer.Abort);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = _createOutput();
        void OnStopped(object? sender, StoppedEventArgs args)
        {
            if (attemptToken.IsCancellationRequested)
                stopped.TrySetCanceled(attemptToken);
            else if (args.Exception is not null)
                stopped.TrySetException(new IOException("Speech output device failed.", args.Exception));
            else if (!buffer.IsDrained)
                stopped.TrySetException(new IOException("Speech output stopped before the audio drained."));
            else
                stopped.TrySetResult();
        }
        output.PlaybackStopped += OnStopped;
        Task? producer = null;
        try
        {
            if (output is not IWavePosition position)
                throw new NotSupportedException("Speech playback requires an output device clock.");
            output.Init(new BufferWaveProvider(format, buffer, position));
            if (output.OutputWaveFormat.SampleRate != format.SampleRate ||
                output.OutputWaveFormat.Channels != format.Channels ||
                output.OutputWaveFormat.BitsPerSample != format.BitsPerSample)
                throw new NotSupportedException("Speech output clock must use the negotiated PCM format.");
            producer = ProduceAsync();
            // A producer failure before enough audio arrives must not leave Ready waiting forever.
            await Task.WhenAny(buffer.Ready, producer).WaitAsync(attemptToken).ConfigureAwait(false);
            if (producer.IsCompleted)
                await producer.ConfigureAwait(false);
            await buffer.Ready.WaitAsync(attemptToken).ConfigureAwait(false);
            attemptToken.ThrowIfCancellationRequested();
            output.Play();
            reportPhase?.Invoke(PcmPlaybackPhase.Playing);
            await Task.WhenAny(producer, stopped.Task).WaitAsync(attemptToken).ConfigureAwait(false);
            if (stopped.Task.IsCompleted)
                await stopped.Task.ConfigureAwait(false);
            await producer.WaitAsync(attemptToken).ConfigureAwait(false);
            // BufferWaveProvider withholds EOF until the clock passes the final audio byte.
            // Then WasapiOut raises PlaybackStopped after its final period and Stop/Reset.
            await stopped.Task.WaitAsync(attemptToken).ConfigureAwait(false);
            return new PcmPlaybackResult(buffer.TotalBytes, buffer.Underruns);
        }
        finally
        {
            buffer.Abort();
            lifetime.Cancel();
            output.PlaybackStopped -= OnStopped;
            output.Stop();
            if (producer is not null)
            {
                // Observe a late failure even if provider shutdown cannot complete immediately.
                _ = producer.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        async Task ProduceAsync()
        {
            var receivedAudio = false;
            await produceAudio(async (bytes, token) =>
            {
                await buffer.WriteAsync(bytes, token).ConfigureAwait(false);
                if (!receivedAudio && bytes.Length > 0)
                {
                    receivedAudio = true;
                    reportPhase?.Invoke(PcmPlaybackPhase.Buffering);
                }
            }, attemptToken).ConfigureAwait(false);
            attemptToken.ThrowIfCancellationRequested();
            buffer.Complete();
        }
    }

    private sealed class BufferWaveProvider(PcmAudioFormat format, PcmPlaybackBuffer buffer, IWavePosition position) : IWaveProvider
    {
        private long _submittedBytes;
        private long? _audioEnd;
        public WaveFormat WaveFormat { get; } = new(format.SampleRate, format.BitsPerSample, format.Channels);

        public int Read(byte[] destination, int offset, int count)
        {
            int read = buffer.Read(destination.AsSpan(offset, count));
            if (read > 0)
            {
                _submittedBytes += read;
                return read;
            }
            if (!buffer.IsDrained || count == 0)
                return 0;
            // WasapiOut's final sleep alone is not a padding/drain guarantee. Its GetPosition
            // converts hardware clock ticks to OutputWaveFormat bytes, matching shared PCM.
            // Freeze the endpoint before adding tail silence; never chase a moving target.
            _audioEnd ??= _submittedBytes;
            if (position.GetPosition() >= _audioEnd.Value)
                return 0;
            Array.Clear(destination, offset, count);
            return count;
        }
    }
}
