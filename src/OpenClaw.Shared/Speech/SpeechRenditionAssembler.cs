using System.Text;

namespace OpenClaw.Shared.Speech;

public enum SpeechDeliveryChange { Began, Appended, Terminal, Duplicate }

/// <summary>
/// One rendition's ordered receipt. The connection owner supplies the expected identity and
/// invalidates this object on session/connection replacement; it is not a transcript store.
/// Calls must be serialized by that owner. Only Appended makes text eligible for live synthesis.
/// </summary>
public sealed class SpeechRenditionAssembler(SpeechRenditionIdentity expectedIdentity)
{
    public const int MaxEvents = 4096;
    private readonly Dictionary<int, SpeechDeliveryEvent> _received = [];
    private readonly StringBuilder _text = new();
    private int _textBytes;
    private bool _invalidated;

    public SpeechRenditionIdentity Identity { get; } = expectedIdentity;
    public string Text => _text.ToString();
    public string? Outcome { get; private set; }
    public string? Reason { get; private set; }
    public string? MessageId { get; private set; }
    public bool IsComplete => !_invalidated && Outcome == "complete";

    public SpeechDeliveryChange Accept(SpeechDeliveryEvent value)
    {
        try
        {
            if (_invalidated) throw Invalid();
            SpeechProtocol.Validate(value);
            if (value.Identity != Identity) throw Invalid();
            if (_received.TryGetValue(value.Sequence, out var previous))
            {
                if (value != previous) throw Invalid();
                return SpeechDeliveryChange.Duplicate;
            }
            if (Outcome is not null || value.Sequence != _received.Count || _received.Count >= MaxEvents)
                throw Invalid();
            if (value.Kind == "segment")
            {
                int bytes = Encoding.UTF8.GetByteCount(value.Text!);
                if (bytes > SpeechProtocol.MaxTextBytes - _textBytes) throw Invalid();
                _textBytes += bytes;
                _text.Append(value.Text);
            }
            else if (value.Kind == "terminal")
            {
                if (value.Outcome == "complete" && !string.Equals(value.Text, Text, StringComparison.Ordinal))
                    throw Invalid();
                Outcome = value.Outcome;
                Reason = value.Reason;
                MessageId = value.MessageId;
            }
            _received.Add(value.Sequence, value);
            return value.Kind switch
            {
                "begin" => SpeechDeliveryChange.Began,
                "segment" => SpeechDeliveryChange.Appended,
                _ => SpeechDeliveryChange.Terminal
            };
        }
        catch (SpeechProtocolException)
        {
            Invalidate();
            throw;
        }
    }

    public void Invalidate()
    {
        _invalidated = true;
        Outcome = "cancelled";
        Reason = null;
        MessageId = null;
        _received.Clear();
        _text.Clear();
        _textBytes = 0;
    }

    private static SpeechProtocolException Invalid() => new("Speech delivery is stale, incomplete, conflicting or exceeds a limit.");
}
