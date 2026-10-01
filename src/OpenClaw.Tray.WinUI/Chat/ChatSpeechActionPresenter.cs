namespace OpenClawTray.Chat;

/// <summary>Routes explicit speech actions without interpreting speech text.</summary>
internal static class ChatSpeechActionPresenter
{
    public static Task<SpeechAttemptResult> HandleAsync(ChatSpeechAction action)
    {
        if (Microsoft.UI.Xaml.Application.Current is not App app)
            return Task.FromResult(new SpeechAttemptResult(SpeechAttemptOutcome.Skipped, "Dialog is unavailable."));
        app.SelectChatSpeechSession(action.SessionKey);
        return action.Kind == ChatSpeechActionKind.ReadWritten
            ? app.ReadWrittenChatAnswerAsync(action.SessionKey, action.EntryId, action.WrittenText)
            : action.Rendition is { } rendition
                ? app.ReplayChatSpeechAsync(rendition)
                : Task.FromResult(new SpeechAttemptResult(SpeechAttemptOutcome.Skipped, "No complete composed speech is stored for this response."));
    }
}
