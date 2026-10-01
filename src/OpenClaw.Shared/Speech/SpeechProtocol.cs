using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Speech;

public sealed record SpeechProtocolVersion(
    [property: JsonPropertyName("major"), JsonRequired] int Major,
    [property: JsonPropertyName("minor"), JsonRequired] int Minor);

/// <summary>
/// Immutable rendition identity. Gateway/connection identity is supplied by the authenticated
/// transport owner, never accepted from an event as authority. ResponseId is stable during
/// generation; the terminal messageId maps it to the host's persisted assistant entry.
/// </summary>
public sealed record SpeechRenditionIdentity(
    [property: JsonPropertyName("sessionKey"), JsonRequired] string SessionKey,
    [property: JsonPropertyName("sessionIncarnation"), JsonRequired] string SessionIncarnation,
    [property: JsonPropertyName("responseId"), JsonRequired] string ResponseId,
    [property: JsonPropertyName("renditionId"), JsonRequired] string RenditionId);

public sealed record SpeechDeliveryEvent(
    [property: JsonPropertyName("protocol"), JsonRequired] SpeechProtocolVersion Protocol,
    [property: JsonPropertyName("identity"), JsonRequired] SpeechRenditionIdentity Identity,
    [property: JsonPropertyName("sequence"), JsonRequired] int Sequence,
    [property: JsonPropertyName("kind"), JsonRequired] string Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("outcome")] string? Outcome = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("origin")] string? Origin = null,
    [property: JsonPropertyName("cueFormat")] string? CueFormat = null,
    [property: JsonPropertyName("messageId")] string? MessageId = null);

public sealed class SpeechProtocolException(string message) : Exception(message);

/// <summary>Validates untrusted plugin messages before they become eligible for synthesis.</summary>
public static class SpeechProtocol
{
    public const int MajorVersion = 1;
    public const int MaxTextBytes = 64 * 1024;
    public const int MaxSegmentBytes = 4 * 1024;
    public const int MaxEventBytes = 96 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SpeechDeliveryEvent ParseEvent(JsonElement element)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object || StrictUtf8.GetByteCount(element.GetRawText()) > MaxEventBytes)
                throw Invalid();
            var value = element.Deserialize<SpeechDeliveryEvent>();
            if (value is null) throw Invalid();
            Validate(value);
            return value;
        }
        catch (Exception error) when (error is JsonException or EncoderFallbackException)
        {
            // Raw provider content and decoder exception messages must not enter diagnostics.
            throw Invalid();
        }
    }

    public static void Validate(SpeechDeliveryEvent value)
    {
        if (value.Protocol is null || value.Protocol.Major != MajorVersion || value.Protocol.Minor < 0)
            throw new SpeechProtocolException("The speech protocol version is incompatible.");
        if (value.Identity is not { } identity || !Identifier(identity.SessionKey) ||
            !Identifier(identity.SessionIncarnation) || !Identifier(identity.ResponseId) || !Identifier(identity.RenditionId) ||
            value.Sequence < 0 || (value.MessageId is not null && !Identifier(value.MessageId)))
            throw Invalid();
        switch (value.Kind)
        {
            case "begin" when value.Sequence == 0 && value.Origin == "composed" && value.CueFormat == "elevenlabs-audio-tags"
                && value.Text is null && value.Outcome is null && value.Reason is null:
                return;
            case "segment" when value.Sequence > 0 && value.Text is { Length: > 0 } && value.Outcome is null && value.Reason is null:
                ValidateText(value.Text, MaxSegmentBytes);
                return;
            case "terminal" when value.Sequence > 0 && value.Outcome is "complete" or "unavailable" or "cancelled" or "failed":
                if (value.Outcome == "complete")
                {
                    if (string.IsNullOrWhiteSpace(value.Text) || value.Reason is not null || !Identifier(value.MessageId))
                        throw Invalid();
                    ValidateText(value.Text, MaxTextBytes);
                }
                else if (value.Text is not null || !Identifier(value.Reason))
                    throw Invalid();
                return;
            default:
                throw Invalid();
        }
    }

    private static bool Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { return false; }
        // JSON Schema maxLength counts Unicode code points, not UTF-16 code units.
        return value.EnumerateRunes().Take(513).Count() <= 512;
    }

    private static void ValidateText(string text, int limit)
    {
        try
        {
            if (text.Contains('\r') || StrictUtf8.GetByteCount(text) > limit) throw Invalid();
        }
        catch (EncoderFallbackException) { throw Invalid(); }
    }

    private static SpeechProtocolException Invalid() => new("The speech event is malformed or exceeds a limit.");
}
