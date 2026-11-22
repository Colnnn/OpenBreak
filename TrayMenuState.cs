namespace Afterbreak;

internal enum TrayCommand
{
    Capture = 1,
    Preview,
    EndBreak,
    Quit,
    PreviewSampleBoard,
    Tasks,
    Settings,
    TogglePause,
    Snooze,
    Skip,
    CaptureSettings,
    Appearance,
    OpenMain,
    ToggleFloatingTimer,
}

internal readonly record struct TrayMenuItem(TrayCommand Command, string Label, bool Visible = true, bool Enabled = true)
{
    internal bool Selectable => Visible && Enabled;
}

internal readonly record struct TrayMenuState(string? Shortcut, bool CanPreview, string? DismissLabel,
    string ReminderStatus, bool ManuallyPaused, bool SnoozeAvailable, bool SkipAvailable,
    int SnoozeSeconds = ReminderSettings.DefaultSnoozeSeconds, string? CountdownStatus = null,
    bool? FloatingTimerHidden = null)
{
    internal TrayMenuItem[] Items() =>
    [
        new(TrayCommand.OpenMain, "Open Afterbreak"),
        new(TrayCommand.Capture, Shortcut is { } shortcut ? $"Capture task · {shortcut}" : "Capture task"),
        new(TrayCommand.Preview, "Preview a break", Enabled: CanPreview),
        new(TrayCommand.PreviewSampleBoard, "Preview sample board", Enabled: CanPreview),
        new(TrayCommand.EndBreak, DismissLabel ?? "", Visible: DismissLabel is not null),
        new(TrayCommand.Settings, "Settings"),
        new(TrayCommand.ToggleFloatingTimer, FloatingTimerHidden == true ? "Show floating timer" : "Hide floating timer",
            Visible: FloatingTimerHidden.HasValue),
        new(TrayCommand.TogglePause, ManuallyPaused ? "Resume after presentation" : "Pause for presentation"),
        new(TrayCommand.Snooze, $"Snooze {ReminderDuration.Format(SnoozeSeconds)}", Visible: SnoozeAvailable),
        new(TrayCommand.Skip, "Skip break", Visible: SkipAvailable),
        new(TrayCommand.Quit, "Quit Afterbreak"),
    ];

    internal bool CanInvoke(TrayMenuItem offered) => offered.Selectable && Items().Contains(offered);
}

internal sealed record TrayOpening(nint Source, Action<TrayCommand> Dispatch);

internal sealed class TrayMenuLifetime
{
    internal TrayOpening? Current { get; private set; }

    internal void Open(TrayOpening opening) => Current = opening;

    internal bool TryClose(TrayOpening opening)
    {
        if (!ReferenceEquals(Current, opening))
        {
            return false;
        }

        Current = null;
        return true;
    }
}
