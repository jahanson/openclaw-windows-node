
namespace OpenClawTray.Services;

/// <summary>Validated wire format for raw PCM. Compressed or container audio must be decoded first.</summary>
public sealed record PcmAudioFormat
{
    public PcmAudioFormat(int sampleRate, int channels = 1, int bitsPerSample = 16,
        string encoding = "pcm_s16le", string contentType = "audio/pcm")
    {
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (bitsPerSample != 16 || encoding != "pcm_s16le" || contentType != "audio/pcm")
            throw new NotSupportedException("Speech playback requires signed 16-bit little-endian raw PCM.");
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        Encoding = encoding;
        ContentType = contentType;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public string Encoding { get; }
    public string ContentType { get; }
    public int BlockAlign => Channels * (BitsPerSample / 8);
    public int BytesPerSecond => checked(SampleRate * BlockAlign);
}
