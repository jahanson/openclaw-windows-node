using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record GatewaySessionInvalidation(string SessionKey, string Reason, string? RunId = null);

public partial class OpenClawGatewayClient
{
    /// <summary>
    /// Raised synchronously before a local destructive request or authoritative remote history change.
    /// Local request failure never resumes old audio; resets may preserve the physical session ID.
    /// </summary>
    public event EventHandler<GatewaySessionInvalidation>? SessionInvalidating;

    private void NotifySessionMutationStarting(string method, object? parameters)
    {
        if (method is not ("sessions.reset" or "sessions.delete" or "sessions.compaction.restore" or "chat.abort")) return;
        var value = JsonSerializer.SerializeToElement(parameters);
        var keyName = method == "chat.abort" ? "sessionKey" : "key";
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(keyName, out var key) ||
            key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString())) return;
        var runId = method == "chat.abort" && value.TryGetProperty("runId", out var run) && run.ValueKind == JsonValueKind.String
            ? run.GetString() : null;
        RaiseSessionInvalidation(new GatewaySessionInvalidation(key.GetString()!, method, runId));
    }

    private void NotifyRemoteSessionMutation(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
            reason.GetString() is not ("reset" or "delete" or "archive" or "rewind" or "branch-switch" or "checkpoint-restore") ||
            !payload.TryGetProperty("sessionKey", out var key) ||
            key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString())) return;
        RaiseSessionInvalidation(new GatewaySessionInvalidation(key.GetString()!, $"remote-session-{reason.GetString()}"));
    }

    private void RaiseSessionInvalidation(GatewaySessionInvalidation invalidation)
    {
        if (SessionInvalidating is not { } subscribers) return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<EventHandler<GatewaySessionInvalidation>>())
        {
            try { subscriber(this, invalidation); }
            catch (Exception error) { _logger.Warn($"Session invalidation observer failed ({error.GetType().Name})."); }
        }
    }
}
