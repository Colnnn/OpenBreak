namespace Afterbreak;

[Flags]
internal enum FocusAction
{
    None = 0,
    Start = 1,
    Stop = 2,
    Pause = 4,
    Resume = 8,
    Snooze = 16,
    Skip = 32,
    EndBreak = 64,
    Preview = 128,
    CancelPreview = 256,
}

internal sealed record FocusSnapshot(bool Enabled, ReminderPhase Phase, int Seconds, ReminderPause PausedBy,
    int CompletedShorts, int UpcomingBreakSeconds, BoardTask? NextTask, bool TaskUnavailable,
    bool RemindersUnavailable, ReminderPhase? PreviewPhase, int PreviewSeconds, FocusAction Actions,
    bool LongBreaks = true, int ShortsBeforeLong = ReminderSettings.DefaultShortsBeforeLong,
    int SnoozeSeconds = ReminderSettings.DefaultSnoozeSeconds)
{
    internal bool NextBreakIsLong => LongBreaks && CompletedShorts >= ShortsBeforeLong;
}
