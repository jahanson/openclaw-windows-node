using System.Globalization;

namespace OpenClawTray.Services;

public sealed class WrittenAnswerReadingException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Explicit long written-answer reading. Only one small section is admitted to the
/// provider ahead of playback. The next section waits for the player's hardware drain,
/// not merely its application buffer. The caller retains one playback lease throughout.
/// </summary>
public sealed class WrittenAnswerSpeechPlayback(ElevenLabsDialogClient client, WasapiPcmPlayback playback)
{
    public const int MaxTextCharacters = 10000;
    public const int SegmentCharacters = 600;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);

    public static bool RequiresStreaming(string text) => text.Length > ElevenLabsDialogClient.MaxTextCharacters;

    public async Task PlayAsync(ElevenLabsDialogRequest request, string text,
        CancellationToken cancellationToken, Action<PcmPlaybackPhase>? reportPhase = null)
    {
        // Preflight the entire bounded text before spending any credits. Splits preserve
        // every character, including whitespace and extended Unicode grapheme clusters.
        var sections = Split(text);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(Deadline);
        long audioBytes = 0;
        try
        {
            foreach (var section in sections)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                await playback.PlayAsync(ElevenLabsDialogClient.AudioFormat, (write, token) =>
                    client.StreamAsync(request, OneSection(section), async (audio, audioToken) =>
                    {
                        audioBytes += audio.Length;
                        if (audioBytes > ElevenLabsDialogClient.MaxAudioBytes)
                            throw new WrittenAnswerReadingException("written-answer-audio-limit");
                        await write(audio, audioToken).ConfigureAwait(false);
                    }, token), lifetime.Token, reportPhase).ConfigureAwait(false);
            }
        }
        catch (DialogProviderException error) when (error.Reason == DialogProviderFailure.Limit)
        {
            // Every section passed the stricter text preflight. Provider-side limits
            // here concern audio/frame buffering, not the composed rendition contract.
            throw new WrittenAnswerReadingException("written-answer-audio-limit");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A player/provider deadline is a failure, not a user pressing Stop.
            throw new WrittenAnswerReadingException(lifetime.IsCancellationRequested
                ? "written-answer-time-limit" : "provider-unavailable");
        }
    }

    private static string[] Split(string text)
    {
        if (text.Length is 0 or > MaxTextCharacters)
            throw new WrittenAnswerReadingException("written-answer-text-limit");
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[++i]))
                throw new WrittenAnswerReadingException("written-answer-invalid-text");
        }
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
        var sections = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var maximum = Math.Min(start + SegmentCharacters, text.Length);
            var end = boundaries.Last(value => value <= maximum);
            if (end == start) throw new WrittenAnswerReadingException("written-answer-text-limit");
            if (end < text.Length)
            {
                // Prefer a substantial sentence, then a word, rather than tiny fragments.
                var candidates = boundaries.Where(value => value > start + SegmentCharacters / 2 && value <= end).ToArray();
                var sentence = candidates.LastOrDefault(value => text[value - 1] is '.' or '!' or '?' or '\n');
                var word = candidates.LastOrDefault(value => char.IsWhiteSpace(text[value - 1]));
                if (sentence > start) end = sentence;
                else if (word > start) end = word;
            }
            sections.Add(text[start..end]);
            start = end;
        }
        return sections.ToArray();
    }

    private static async IAsyncEnumerable<string> OneSection(string text)
    {
        yield return text;
        await Task.CompletedTask;
    }
}
