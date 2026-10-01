using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatSpeechSurfaceSelectionTests
{
    [Fact]
    public void ClosingTrayRestoresVisibleHubAndClosingLastSurfaceClearsSelection()
    {
        var selection = new ChatSpeechSurfaceSelection();
        var hub = new object();
        var tray = new object();
        Assert.Equal("hub-session", selection.Update(hub, "hub-session", true));
        Assert.Equal("tray-session", selection.Update(tray, "tray-session", true));
        Assert.Equal("tray-session", selection.Update(hub, "hub-session", true));
        Assert.Equal("hub-session", selection.Update(tray, "tray-session", false));
        Assert.Null(selection.Update(hub, "hub-session", false));
    }

    [Fact]
    public void ExplicitFocusWinsAndInactiveSurfaceCannotClaimForeground()
    {
        var selection = new ChatSpeechSurfaceSelection();
        var first = new object();
        var second = new object();
        selection.Update(first, "first", true);
        selection.Update(second, "second", true);
        Assert.Equal("first", selection.Update(first, "first", true, true));
        Assert.Equal("first", selection.Update(second, "hidden", false, true));
    }
}
