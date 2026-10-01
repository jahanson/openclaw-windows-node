using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

internal static class HubCommandActionDispatcher
{
    public static bool TryDispatch(HubCommandAction action, IAppCommands commands)
    {
        switch (action.Kind)
        {
            case HubCommandActionKind.OpenChat:
                commands.ShowChat();
                return true;
            case HubCommandActionKind.OpenDashboard:
                commands.OpenDashboard(action.Value);
                return true;
            default:
                return false;
        }
    }
}
