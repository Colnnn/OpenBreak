namespace Afterbreak;

internal enum BreakDisplay { SavedTaskBoard = 0, OneTask = 1, TimerOnly = 2 }

internal sealed record ReminderSettings(bool Enabled, int WorkSeconds, int ShortSeconds, int LongSeconds,
    bool WorkingHours, int StartMinute, int EndMinute, BreakDisplay BreakDisplay)
{
    internal static ReminderSettings Default { get; } = new(false, 1800, 30, 300, false, 9 * 60, 17 * 60, BreakDisplay.SavedTaskBoard);

    internal bool IsValid => WorkSeconds is >= 5 and <= 86400 && ShortSeconds is >= 1 and <= 3600 &&
        LongSeconds is >= 1 and <= 3600 && StartMinute is >= 0 and < 1440 && EndMinute is >= 0 and < 1440 &&
        (!WorkingHours || StartMinute != EndMinute) && Enum.IsDefined(BreakDisplay);

    internal TimeSpan WorkLength => TimeSpan.FromSeconds(WorkSeconds);
    internal TimeSpan BreakLength(int completedShorts) => TimeSpan.FromSeconds(completedShorts == 3 ? LongSeconds : ShortSeconds);

    // The start date identifies a working window even when it spans midnight.
    internal DateOnly? WindowOn(DateTimeOffset local)
    {
        if (!WorkingHours)
        {
            return DateOnly.MinValue;
        }

        var day = DateOnly.FromDateTime(local.DateTime);
        var minute = local.Hour * 60 + local.Minute;
        if (StartMinute < EndMinute)
        {
            return minute >= StartMinute && minute < EndMinute ? day : null;
        }

        return minute >= StartMinute ? day : minute < EndMinute && day > DateOnly.MinValue ? day.AddDays(-1) : null;
    }
}

[Flags]
internal enum ReminderPause
{
    None = 0,
    Manual = 1,
    Locked = 2,
    Suspended = 4,
    Presentation = 8,
    Interaction = 16,
    OutsideHours = 32,
    Work = 64,
}

internal enum ReminderPhase { Work, Snoozed, Warning, Break }

internal sealed record ReminderCheckpoint(ReminderPhase Phase, TimeSpan Remaining, int CompletedShorts,
    bool ManuallyPaused, TimeSpan Away, DateTimeOffset SavedAt)
{
    internal bool WorkPaused { get; init; }
}

internal sealed class ReminderScheduler
{
    internal static readonly TimeSpan WarningLength = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SnoozeLength = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongTickGap = TimeSpan.FromSeconds(5);
    private const ReminderPause AwayReasons = ReminderPause.Manual | ReminderPause.Locked | ReminderPause.Suspended | ReminderPause.Presentation;
    private readonly TimeProvider clock;
    private long lastTick;
    private DateOnly? workingWindow;
    private TimeSpan away;
    private TimeSpan? interruptedBreak;

    internal ReminderScheduler(TimeProvider clock, ReminderSettings settings, ReminderCheckpoint? saved = null)
    {
        this.clock = clock;
        Settings = settings;
        lastTick = clock.GetTimestamp();
        workingWindow = settings.WindowOn(clock.GetLocalNow());
        Remaining = settings.WorkLength;
        ManuallyPaused = saved?.ManuallyPaused ?? false;
        PausedBy = ManuallyPaused ? ReminderPause.Manual : ReminderPause.None;
        if (saved is null || !settings.Enabled)
        {
            return;
        }

        CompletedShorts = saved.CompletedShorts;
        if (saved.WorkPaused)
        {
            Phase = saved.Phase;
            Remaining = saved.Remaining;
            PausedBy |= ReminderPause.Work;
            return;
        }
        var elapsed = clock.GetUtcNow() - saved.SavedAt;
        var previousWindow = settings.WindowOn(TimeZoneInfo.ConvertTime(saved.SavedAt, clock.LocalTimeZone));
        if (elapsed >= TimeSpan.Zero && elapsed + saved.Away < BreakLength && workingWindow is not null &&
            workingWindow == previousWindow && saved.Remaining > TimeSpan.Zero &&
            saved.Phase is ReminderPhase.Work or ReminderPhase.Snoozed)
        {
            Phase = saved.Phase;
            // A duration change during a work pause is applied to subsequent intervals.
            Remaining = Phase == ReminderPhase.Snoozed && saved.Remaining > SnoozeLength ? SnoozeLength : saved.Remaining;
            away = elapsed + saved.Away;
        }
    }

    internal ReminderSettings Settings { get; private set; }
    internal ReminderPhase Phase { get; private set; }
    internal ReminderPause PausedBy { get; private set; }
    internal TimeSpan Remaining { get; private set; }
    internal bool ManuallyPaused { get; private set; }
    internal bool WorkPaused => (PausedBy & ReminderPause.Work) != 0;
    internal int CompletedShorts { get; private set; }
    internal bool IsLongBreak => CompletedShorts == 3;
    private TimeSpan BreakLength => Settings.BreakLength(CompletedShorts);

    internal void Update(ReminderPause environment)
    {
        var now = clock.GetTimestamp();
        var elapsed = clock.GetElapsedTime(lastTick, now);
        lastTick = now;
        if (!Settings.Enabled)
        {
            return;
        }

        var window = Settings.WindowOn(clock.GetLocalNow());
        var workPaused = WorkPaused;
        var pauses = environment | (window is null ? ReminderPause.OutsideHours : ReminderPause.None) |
            (ManuallyPaused ? ReminderPause.Manual : ReminderPause.None) |
            (workPaused ? ReminderPause.Work : ReminderPause.None);
        if (window != workingWindow)
        {
            if (!workPaused)
            {
                StartWork();
            }
            away = TimeSpan.Zero;
            workingWindow = window;
            elapsed = TimeSpan.Zero;
        }

        if (workPaused)
        {
            PausedBy = pauses;
            return;
        }

        if ((PausedBy & AwayReasons) != 0 || (PausedBy == ReminderPause.None && elapsed > LongTickGap))
        {
            away += elapsed;
        }
        else if (PausedBy == ReminderPause.None)
        {
            Remaining -= elapsed;
            if (Remaining < TimeSpan.Zero)
            {
                Remaining = TimeSpan.Zero;
            }
        }
        PausedBy = pauses;

        if (away >= BreakLength)
        {
            StartWork();
            away = TimeSpan.Zero;
        }
        else if (pauses != ReminderPause.None || elapsed > LongTickGap)
        {
            InterruptVisiblePhase();
        }

        if (pauses != ReminderPause.None)
        {
            return;
        }

        away = TimeSpan.Zero;
        if (Remaining > TimeSpan.Zero)
        {
            return;
        }

        switch (Phase)
        {
            case ReminderPhase.Work:
            case ReminderPhase.Snoozed:
                Start(ReminderPhase.Warning, WarningLength);
                break;
            case ReminderPhase.Warning:
                Start(ReminderPhase.Break, interruptedBreak ?? BreakLength);
                interruptedBreak = null;
                break;
            case ReminderPhase.Break:
                CompletedShorts = IsLongBreak ? 0 : CompletedShorts + 1;
                StartWork();
                break;
        }
    }

    internal void Snooze()
    {
        if (Phase == ReminderPhase.Warning)
        {
            Start(ReminderPhase.Snoozed, SnoozeLength);
        }
    }

    internal void Skip()
    {
        if (Phase is ReminderPhase.Warning or ReminderPhase.Snoozed or ReminderPhase.Break)
        {
            StartWork();
        }
    }

    internal void SetManualPause(bool paused) => ManuallyPaused = paused;

    internal bool SetWorkPause(bool paused, ReminderPause environment)
    {
        if (!Settings.Enabled || Phase is not (ReminderPhase.Work or ReminderPhase.Snoozed) || WorkPaused == paused)
        {
            return false;
        }

        Update(environment);
        if (Phase is not (ReminderPhase.Work or ReminderPhase.Snoozed) || Remaining <= TimeSpan.Zero)
        {
            return false;
        }

        PausedBy = paused ? PausedBy | ReminderPause.Work : PausedBy & ~ReminderPause.Work;
        away = TimeSpan.Zero;
        return true;
    }

    internal ReminderScheduler Copy() => (ReminderScheduler)MemberwiseClone();

    internal void StartVisibleCountdown() => lastTick = clock.GetTimestamp();

    internal ReminderScheduler WithSettings(ReminderSettings settings)
    {
        var candidate = Copy();
        candidate.ApplySettings(settings);
        return candidate;
    }

    private void ApplySettings(ReminderSettings settings)
    {
        var old = Settings;
        Settings = settings;
        if (old with { BreakDisplay = settings.BreakDisplay } == settings)
        {
            return;
        }

        if (settings.Enabled && WorkPaused)
        {
            return;
        }

        if (!old.Enabled || !settings.Enabled)
        {
            CompletedShorts = 0;
        }
        StartWork();
        workingWindow = settings.WindowOn(clock.GetLocalNow());
        PausedBy = ManuallyPaused ? ReminderPause.Manual : ReminderPause.None;
        away = TimeSpan.Zero;
    }

    internal ReminderCheckpoint Checkpoint() => new(Phase, Remaining, CompletedShorts, ManuallyPaused, away, clock.GetUtcNow())
    {
        WorkPaused = WorkPaused,
    };

    private void InterruptVisiblePhase()
    {
        if (Phase == ReminderPhase.Break && Remaining > TimeSpan.Zero)
        {
            interruptedBreak = Remaining;
        }
        if (Phase is ReminderPhase.Warning or ReminderPhase.Break)
        {
            Start(ReminderPhase.Warning, WarningLength);
        }
    }

    private void StartWork()
    {
        interruptedBreak = null;
        Start(ReminderPhase.Work, Settings.WorkLength);
    }

    private void Start(ReminderPhase phase, TimeSpan remaining)
    {
        Phase = phase;
        Remaining = remaining;
        lastTick = clock.GetTimestamp();
    }
}
