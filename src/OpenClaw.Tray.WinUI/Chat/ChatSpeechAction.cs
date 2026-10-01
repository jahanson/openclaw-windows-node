using OpenClaw.Shared.Speech;

namespace OpenClawTray.Chat;

public enum ChatSpeechActionKind { Replay, Transcript, ReadWritten }

/// <summary>Explicit user action with response identity; composed speech never passes through Markdown cleanup.</summary>
public sealed record ChatSpeechAction(string SessionKey, string EntryId, string WrittenText,
    SpeechRendition? Rendition, ChatSpeechActionKind Kind);
