using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

public sealed record ChatDialogSpeechActionsProps(ChatSpeechAction Action,
    Func<ChatSpeechAction, Task<SpeechAttemptResult>> Execute, Action? Stop);

/// <summary>Controlled speech dialogs and immediate click admission for one response's actions.</summary>
public sealed class ChatDialogSpeechActions : Component<ChatDialogSpeechActionsProps>
{
    public override Element Render()
    {
        var props = Props;
        var (busy, setBusy) = UseState(false, threadSafe: true);
        var (dialogText, setDialogText) = UseState<string?>(null, threadSafe: true);
        var active = UseRef(false);
        var operation = UseRef(0);
        var mounted = UseRef(true);
        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () => { mounted.Current = false; operation.Current++; };
        }), Array.Empty<object>());

        async Task PlayAsync(ChatSpeechActionKind kind)
        {
            // Component state may not render before a second click. Admit synchronously.
            if (active.Current) return;
            active.Current = true;
            var current = ++operation.Current;
            setBusy(true);
            try
            {
                var result = await props.Execute(props.Action with { Kind = kind });
                if (mounted.Current && current == operation.Current && result.Outcome is SpeechAttemptOutcome.Failed or SpeechAttemptOutcome.Skipped)
                    setDialogText(OpenClawReactorChatRoot.SpeechReasonLabel(result.Reason) ??
                        L("Chat_Speech_NotReady", "Unavailable"));
            }
            catch (Exception)
            {
                if (mounted.Current && current == operation.Current) setDialogText(L("Chat_Speech_NotReady", "Unavailable"));
            }
            finally
            {
                if (current == operation.Current)
                {
                    active.Current = false;
                    if (mounted.Current) setBusy(false);
                }
            }
        }

        void ReplayOrStop()
        {
            if (active.Current) { props.Stop?.Invoke(); return; }
            if (props.Action.Rendition is null)
                setDialogText(L("Chat_Speech_Unavailable", "No complete composed speech is stored for this response."));
            else _ = PlayAsync(ChatSpeechActionKind.Replay);
        }

        var viewer = ContentDialog(L("Chat_Assistant_Action_SpeechTranscript", "Speech transcript"),
            VStack(12,
                ScrollViewer(TextBlock(dialogText ?? string.Empty).TextWrapping(TextWrapping.Wrap)
                    .HAlign(HorizontalAlignment.Stretch)
                    .Set(text => text.IsTextSelectionEnabled = true)).MaxHeight(400)
                    .HAlign(HorizontalAlignment.Stretch)
                    .Set(scroll =>
                    {
                        scroll.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
                        scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    }),
                TextBlock(L("Chat_Speech_ReplayCost", "Replay generates new audio and may use provider credits.")).TextWrapping(TextWrapping.Wrap),
                Button(TextBlock(L("Chat_Assistant_Action_ReadWritten", "Read written answer")),
                    () => { setDialogText(null); _ = PlayAsync(ChatSpeechActionKind.ReadWritten); })
                    .AutomationName(L("Chat_Assistant_Action_ReadWritten", "Read written answer")).IsEnabled(!busy)),
            L("Chat_Speech_Close", "Close")) with { IsOpen = dialogText is not null, OnClosed = _ => setDialogText(null) };

        return Grid([GridSize.Auto], [GridSize.Auto],
            HStack(4,
                Icon(busy ? "\uE71A" : "\uE767", busy ? L("Chat_Assistant_Action_Stop", "Stop") : L("Chat_Assistant_Action_ReadAloud", "Read aloud"), ReplayOrStop),
                Icon("\uE8A5", L("Chat_Assistant_Action_SpeechTranscript", "Speech transcript"),
                    () => setDialogText(props.Action.Rendition?.Text ?? L("Chat_Speech_Unavailable", "No complete composed speech is stored for this response."))),
                Icon("\uE8F2", L("Chat_Assistant_Action_ReadWritten", "Read written answer"),
                    () => _ = PlayAsync(ChatSpeechActionKind.ReadWritten)).IsEnabled(!busy)),
            viewer);
    }

    private static ButtonElement Icon(string glyph, string label, Action action) =>
        Button(TextBlock(glyph).FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(14), action)
            .Width(32).Height(32).MinWidth(32).MinHeight(32).Padding(0).ToolTip(label).AutomationName(label);

    private static string L(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }
}
