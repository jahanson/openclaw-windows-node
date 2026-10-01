
namespace OpenClawTray.Services;

/// <summary>
/// One attempt's bounded, ordered PCM queue. The device never waits for the producer:
/// underruns emit silence, and zero means confirmed EOF or invalidation, never temporary starvation.
/// </summary>
public sealed class PcmPlaybackBuffer
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly PcmAudioFormat _format;
    private readonly byte[] _bytes;
    private readonly int _startBytes;
    private readonly int _highBytes;
    private readonly int _lowBytes;
    private readonly long _maximumBytes;
    private readonly TaskCompletionSource _ready = NewSignal();
    private TaskCompletionSource _changed = NewSignal();
    private int _readOffset;
    private int _count;
    private long _totalBytes;
    private int _underruns;
    private bool _completed;
    private bool _aborted;

    public PcmPlaybackBuffer(PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _format = format;
        _startBytes = format.BytesPerSecond / 4;
        _highBytes = checked(format.BytesPerSecond * 5);
        _lowBytes = checked(format.BytesPerSecond * 2);
        _bytes = new byte[checked(format.BytesPerSecond * 10)];
        _maximumBytes = Math.Min(32L * 1024 * 1024, (long)format.BytesPerSecond * 180);
    }

    public Task Ready => _ready.Task;
    public int BufferedBytes { get { lock (_gate) return _count; } }
    public long TotalBytes { get { lock (_gate) return _totalBytes; } }
    public int Underruns { get { lock (_gate) return _underruns; } }
    public bool IsDrained { get { lock (_gate) return _completed && !_aborted && _count == 0; } }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ThrowIfClosed(cancellationToken);
                if (audio.Length > _maximumBytes - _totalBytes)
                    throw new InvalidDataException("Speech audio exceeded its byte or duration limit.");
                _totalBytes += audio.Length;
            }
            var backpressured = false;
            while (!audio.IsEmpty)
            {
                Task? wait = null;
                lock (_gate)
                {
                    ThrowIfClosed(cancellationToken);
                    if (_count >= _highBytes || (backpressured && _count > _lowBytes))
                    {
                        backpressured = true;
                        wait = _changed.Task;
                    }
                    else
                    {
                        backpressured = false;
                        int writeOffset = (_readOffset + _count) % _bytes.Length;
                        int length = Math.Min(audio.Length, Math.Min(_highBytes - _count, _bytes.Length - writeOffset));
                        audio.Span[..length].CopyTo(_bytes.AsSpan(writeOffset, length));
                        _count += length;
                        audio = audio[length..];
                        if (_count >= _startBytes)
                            _ready.TrySetResult();
                    }
                }
                if (wait is not null)
                    await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _writer.Release(); }
    }

    /// <summary>Called only after the producer has successfully finalized and delivered all output.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            ThrowIfClosed(CancellationToken.None);
            if (_totalBytes == 0 || _totalBytes % _format.BlockAlign != 0)
                throw new InvalidDataException("Speech audio was empty or ended within a PCM frame.");
            _completed = true;
            _ready.TrySetResult();
            Pulse();
        }
    }

    public int Read(Span<byte> destination)
    {
        if (destination.Length % _format.BlockAlign != 0)
            throw new ArgumentException("Audio reads must contain whole PCM frames.", nameof(destination));
        lock (_gate)
        {
            if (_aborted || destination.IsEmpty)
                return 0;
            int length = Math.Min(destination.Length, _count - _count % _format.BlockAlign);
            int first = Math.Min(length, _bytes.Length - _readOffset);
            _bytes.AsSpan(_readOffset, first).CopyTo(destination);
            _bytes.AsSpan(0, length - first).CopyTo(destination[first..]);
            _readOffset = (_readOffset + length) % _bytes.Length;
            _count -= length;
            Pulse();
            if (length < destination.Length && !_completed)
            {
                destination[length..].Clear();
                _underruns++;
                return destination.Length;
            }
            return length;
        }
    }

    /// <summary>Invalidate queued and future bytes synchronously before awaiting device shutdown.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            _aborted = true;
            _count = 0;
            Array.Clear(_bytes);
            _ready.TrySetCanceled();
            Pulse();
        }
    }

    private void ThrowIfClosed(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_aborted)
            throw new OperationCanceledException("Speech playback was invalidated.");
        if (_completed)
            throw new InvalidOperationException("Speech audio input is already complete.");
    }

    private void Pulse()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
