namespace OpenClawTray.Chat;

/// <summary>UI selection intent only. Physical-session authority stays in the conversation state.</summary>
public sealed class ChatSpeechSurfaceSelection
{
    private readonly object _gate = new();
    private readonly Dictionary<object, (string Session, long Order)> _surfaces = new(ReferenceEqualityComparer.Instance);
    private long _order;

    public string? Update(object surface, string? sessionKey, bool active, bool claimForeground = false)
    {
        lock (_gate)
        {
            if (!active || string.IsNullOrWhiteSpace(sessionKey)) _surfaces.Remove(surface);
            else if (!_surfaces.TryGetValue(surface, out var previous) || claimForeground || previous.Session != sessionKey)
                _surfaces[surface] = (sessionKey, ++_order);
            return _surfaces.Count == 0 ? null : _surfaces.Values.MaxBy(value => value.Order).Session;
        }
    }
}
