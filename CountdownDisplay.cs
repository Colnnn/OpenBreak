namespace Afterbreak;

internal readonly record struct CountdownDisplay(string Phase, string Time, string? Glyphs, bool FinalMinute)
{
    internal const ReminderPause PrivacyPauses =
        ReminderPause.Manual | ReminderPause.Locked | ReminderPause.Suspended | ReminderPause.Presentation;

    internal string Status => Time.Length == 0 ? Phase : $"{Phase} · {Time}";

    internal static CountdownDisplay From(FocusSnapshot snapshot)
    {
        if (snapshot.PreviewPhase is null)
        {
            if (snapshot.RemindersUnavailable)
            {
                return new("Reminders unavailable", "", null, false);
            }
            if (!snapshot.Enabled)
            {
                return new("Reminders off", "", null, false);
            }
        }

        var preview = snapshot.PreviewPhase.HasValue;
        var phase = (snapshot.PreviewPhase ?? snapshot.Phase) switch
        {
            ReminderPhase.Warning => preview ? "Preview warning" : "Break warning",
            ReminderPhase.Break => preview ? "Preview break" : snapshot.NextBreakIsLong ? "Long break" : "Short break",
            ReminderPhase.Snoozed => "Snoozed",
            _ => "Work interval",
        };
        var seconds = preview ? snapshot.PreviewSeconds : snapshot.Seconds;
        var paused = !preview && snapshot.PausedBy != ReminderPause.None;
        var minutes = (seconds + 59L) / 60;
        var finalMinute = !paused && seconds < 60;
        var glyphs = paused ? "II" : finalMinute ? $"{seconds:00}" : minutes > 99 ? "99+" : $"{minutes}";
        return new(paused ? $"Paused · {phase}" : phase, FocusSnapshot.FormatTime(seconds), glyphs, finalMinute);
    }

    internal static bool MayFloat(FocusSnapshot snapshot, bool protectedSurface) =>
        !protectedSurface && snapshot.PreviewPhase != ReminderPhase.Break &&
        !(snapshot.Enabled && !snapshot.RemindersUnavailable && snapshot.Phase == ReminderPhase.Break &&
            snapshot.PausedBy == ReminderPause.None) &&
        (snapshot.PausedBy & PrivacyPauses) == 0;
}
