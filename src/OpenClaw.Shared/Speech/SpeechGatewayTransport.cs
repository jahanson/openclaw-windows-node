using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared.Speech;

public sealed record SpeechCapabilities(bool Available, bool Prepared, bool Live, string? Reason);
public sealed record SpeechTurnTicket(string Ticket, long ExpiresAt);
public sealed record SpeechReadBatch(IReadOnlyList<SpeechDeliveryEvent> Events, string State, int RetryAfterMs);

public interface ISpeechGatewayTransport : IDisposable
{
    Task<SpeechCapabilities> GetCapabilitiesAsync(string? sessionKey = null, CancellationToken cancellationToken = default);
    Task<SpeechTurnTicket> RegisterIntentAsync(string sessionKey, string idempotencyKey, bool live, CancellationToken cancellationToken = default);
    Task<SpeechReadBatch> ReadAsync(SpeechTurnTicket ticket, int afterSequence, CancellationToken cancellationToken = default);
    Task DetachAsync(SpeechTurnTicket ticket, CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed plugin operations on one existing authenticated connection. Disconnect permanently
/// invalidates this instance: reconnect requires fresh intent and never resumes automatic speech.
/// </summary>
public sealed class SpeechGatewayTransport : ISpeechGatewayTransport
{
    private readonly IOperatorGatewayClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private int _disposed;

    public SpeechGatewayTransport(IOperatorGatewayClient client)
    {
        _client = client;
        _token = _lifetime.Token;
        client.StatusChanged += OnStatusChanged;
        if (!client.IsConnectedToGateway || !client.HasHandshakeSnapshot) _lifetime.Cancel();
    }

    public async Task<SpeechCapabilities> GetCapabilitiesAsync(string? sessionKey = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionKey is not null) CheckIdentifier(sessionKey);
        var parameters = sessionKey is null ? new { } : (object)new { sessionKey };
        var value = await CallAsync("capabilities", parameters, cancellationToken).ConfigureAwait(false);
        if (!value.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Object ||
            !protocol.TryGetProperty("major", out var major) || !major.TryGetInt32(out var version) ||
            !protocol.TryGetProperty("minor", out var minor) || !minor.TryGetInt32(out var minorVersion) || minorVersion < 0 ||
            version != SpeechProtocol.MajorVersion)
            return new(false, false, false, "incompatible-version");
        var available = Boolean(value, "available");
        return new(available, available && Boolean(value, "prepared"), available && Boolean(value, "live"),
            value.TryGetProperty("reason", out var reason) ? ShortString(reason) : null);
    }

    public async Task<SpeechTurnTicket> RegisterIntentAsync(string sessionKey, string idempotencyKey,
        bool live, CancellationToken cancellationToken = default)
    {
        CheckIdentifier(sessionKey);
        CheckIdentifier(idempotencyKey);
        var value = await CallAsync("intent", new
        {
            protocol = new SpeechProtocolVersion(1, 0), sessionKey, idempotencyKey,
            delivery = live ? "live" : "prepared", maxSpeechCharacters = 2000
        }, cancellationToken).ConfigureAwait(false);
        if (!value.TryGetProperty("ticket", out var ticket) ||
            !value.TryGetProperty("expiresAt", out var expiry) || !expiry.TryGetInt64(out var expiresAt) || expiresAt <= 0)
            throw Invalid();
        return new(ShortString(ticket), expiresAt);
    }

    public async Task<SpeechReadBatch> ReadAsync(SpeechTurnTicket ticket, int afterSequence,
        CancellationToken cancellationToken = default)
    {
        CheckIdentifier(ticket.Ticket);
        if (afterSequence < -1) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var value = await CallAsync("read", new { ticket = ticket.Ticket, afterSequence, maxEvents = 64 },
            cancellationToken).ConfigureAwait(false);
        if (!value.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array ||
            events.GetArrayLength() > 64 || !value.TryGetProperty("state", out var stateElement) ||
            !value.TryGetProperty("retryAfterMs", out var retry) || !retry.TryGetInt32(out var delay) || delay is < 0 or > 5000)
            throw Invalid();
        var state = ShortString(stateElement);
        if (state is not ("pending" or "active" or "terminal")) throw Invalid();
        return new(events.EnumerateArray().Select(SpeechProtocol.ParseEvent).ToArray(), state, Math.Max(25, delay));
    }

    public async Task DetachAsync(SpeechTurnTicket ticket, CancellationToken cancellationToken = default)
    {
        CheckIdentifier(ticket.Ticket);
        var value = await CallAsync("detach", new { ticket = ticket.Ticket }, cancellationToken).ConfigureAwait(false);
        if (!Boolean(value, "detached")) throw Invalid();
    }

    private async Task<JsonElement> CallAsync(string operation, object parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        var result = await _client.SendWizardRequestAsync("expressive-speech." + operation, parameters, 15000)
            .WaitAsync(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        if (result.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(result.GetRawText()) > 384 * 1024)
            throw Invalid();
        return result;
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status != ConnectionStatus.Connected) _lifetime.Cancel();
    }

    private static bool Boolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid();
        return field.GetBoolean();
    }

    private static string ShortString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString()!;
        CheckIdentifier(text);
        return text;
    }

    private static void CheckIdentifier(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 || text.EnumerateRunes().Take(513).Count() > 512)
            throw Invalid();
    }

    private static SpeechProtocolException Invalid() => new("The speech operation returned an invalid response.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.StatusChanged -= OnStatusChanged;
        _lifetime.Cancel();
        // Outstanding event invocations may still hold the handler. Keep this tiny CTS valid
        // until GC so a raced disconnect cannot throw ObjectDisposedException on the client.
    }
}
