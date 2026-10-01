using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Speech;

/// <summary>A complete rendition read through authorized core history, bound to its real message receipt.</summary>
public sealed record SpeechRendition(
    SpeechRenditionIdentity Identity, string MessageId, string Text, string Origin, string CueFormat);

internal sealed record PersistedSpeechRendition(
    [property: JsonPropertyName("protocol"), JsonRequired] SpeechProtocolVersion Protocol,
    [property: JsonPropertyName("identity"), JsonRequired] SpeechRenditionIdentity Identity,
    [property: JsonPropertyName("origin"), JsonRequired] string Origin,
    [property: JsonPropertyName("cueFormat"), JsonRequired] string CueFormat,
    [property: JsonPropertyName("outcome"), JsonRequired] string Outcome,
    [property: JsonPropertyName("text"), JsonRequired] string Text);

internal static class SpeechHistoryReader
{
    // Missing/invalid optional speech must not break the normal written transcript.
    internal static SpeechRendition? Read(JsonElement message, string sessionKey, string? sessionId, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(messageId) ||
            !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
            role.GetString() != "assistant" ||
            !message.TryGetProperty("openclawExpressiveSpeech", out var metadata)) return null;
        try
        {
            if (metadata.ValueKind != JsonValueKind.Object ||
                Encoding.UTF8.GetByteCount(metadata.GetRawText()) > SpeechProtocol.MaxEventBytes) return null;
            var stored = metadata.Deserialize<PersistedSpeechRendition>();
            if (stored?.Identity is not { } identity || identity.SessionKey != sessionKey ||
                identity.SessionIncarnation != sessionId || stored.Origin != "composed" ||
                stored.CueFormat != "elevenlabs-audio-tags" || stored.Outcome != "complete") return null;
            // Reuse the wire content/version limits, binding the receipt from core history,
            // never a message ID asserted by plugin metadata.
            SpeechProtocol.Validate(new(stored.Protocol, identity, 1, "terminal", stored.Text,
                "complete", MessageId: messageId));
            return new(identity, messageId, stored.Text, stored.Origin, stored.CueFormat);
        }
        catch (Exception error) when (error is JsonException or SpeechProtocolException)
        {
            return null;
        }
    }
}
