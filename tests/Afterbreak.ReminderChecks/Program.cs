namespace Afterbreak;

internal static class Program
{
    private static readonly DateTimeOffset Noon = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReminderSettings ShortCycle = ReminderSettings.Default with
    {
        Enabled = true, WorkSeconds = 5, ShortSeconds = 3, LongSeconds = 8,
    };

    private static void Main()
    {
        CadenceAndSnooze();
        OverlappingPauses();
        SleepAndInterruption();
        ClockAndHours();
        Restart();
        ExplicitWorkPause();
        ConfigurableRhythm();
    }

    private static void CadenceAndSnooze()
    {
        var defaults = ReminderSettings.Default;
        Expect(!defaults.Enabled && defaults.WorkSeconds == 1800 && defaults.ShortSeconds == 30 && defaults.LongSeconds == 300,
            "default is opt-out with 30m work, 30s short and 5m long");
        var clock = new TestClock(Noon);
        var scheduler = new ReminderScheduler(clock, ShortCycle);
        Step(scheduler, clock, 5);
        Expect(scheduler.Phase == ReminderPhase.Warning && scheduler.Remaining.TotalSeconds == 10, "work gives a ten-second warning");
        scheduler.Snooze();
        for (var snooze = 0; snooze < 2; snooze++)
        {
            Step(scheduler, clock, 299);
            Expect(scheduler.Phase == ReminderPhase.Snoozed && scheduler.Remaining.TotalSeconds == 1 && scheduler.CompletedShorts == 0,
                "snooze waits five minutes without advancing cadence");
            Step(scheduler, clock, 1);
            Expect(scheduler.Phase == ReminderPhase.Warning && scheduler.Remaining.TotalSeconds == 10,
                "each snoozed reminder returns with a fresh warning");
            if (snooze == 0)
            {
                scheduler.Snooze();
            }
        }
        scheduler.Skip();
        Expect(scheduler.Phase == ReminderPhase.Work && scheduler.CompletedShorts == 0 && scheduler.Remaining.TotalSeconds == 5,
            "skip starts fresh work without advancing cadence");

        for (var count = 0; count < 3; count++)
        {
            Step(scheduler, clock, 15);
            Expect(scheduler.Phase == ReminderPhase.Break && scheduler.Remaining.TotalSeconds == 3, "scheduled short break has the configured duration");
            Step(scheduler, clock, 3);
            Expect(scheduler.CompletedShorts == count + 1, "natural short completion advances cadence once");
        }
        Step(scheduler, clock, 15);
        Expect(scheduler.IsLongBreak && scheduler.Remaining.TotalSeconds == 8, "three completed shorts make the next break long");
        scheduler.Skip();
        Expect(scheduler.IsLongBreak && scheduler.Phase == ReminderPhase.Work, "ending the long break early keeps it due");
        Step(scheduler, clock, 23);
        Expect(scheduler.Phase == ReminderPhase.Work && scheduler.CompletedShorts == 0, "only natural long completion resets the cadence");
    }

    private static void OverlappingPauses()
    {
        var clock = new TestClock(Noon);
        var settings = ShortCycle with { WorkSeconds = 30, ShortSeconds = 10 };
        var scheduler = new ReminderScheduler(clock, settings);
        Step(scheduler, clock, 12);
        scheduler.SetManualPause(true);
        scheduler.Update(ReminderPause.None);
        Step(scheduler, clock, 2, ReminderPause.Locked);
        scheduler.SetManualPause(false);
        Step(scheduler, clock, 4, ReminderPause.Locked | ReminderPause.Presentation);
        Step(scheduler, clock, 2, ReminderPause.Presentation);
        Expect(scheduler.PausedBy == ReminderPause.Presentation && scheduler.Remaining.TotalSeconds == 18,
            "each independent pause clears without resuming through another reason");
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.Remaining.TotalSeconds == 18, "short overlapping absence preserves work remaining");

        scheduler.Update(ReminderPause.Locked);
        Step(scheduler, clock, 5, ReminderPause.Locked | ReminderPause.Presentation);
        Step(scheduler, clock, 5, ReminderPause.Presentation);
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.Remaining.TotalSeconds == 30 && scheduler.CompletedShorts == 0,
            "the union of overlapping away time earns a fresh work interval at the break length");
        Step(scheduler, clock, 12);
        scheduler.Update(ReminderPause.Interaction);
        Step(scheduler, clock, 40, ReminderPause.Interaction);
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.Remaining.TotalSeconds == 18, "a long edit or preview defers without counting as rest");
    }

    private static void SleepAndInterruption()
    {
        var clock = new TestClock(Noon);
        var scheduler = new ReminderScheduler(clock, ShortCycle);
        Step(scheduler, clock, 14);
        Step(scheduler, clock, 1, ReminderPause.Suspended);
        Expect(scheduler.PausedBy == ReminderPause.Suspended && scheduler.Phase == ReminderPhase.Warning &&
            scheduler.Remaining.TotalSeconds == 10, "suspend at warning expiry prevents the stale break");
        clock.Advance(TimeSpan.FromHours(2));
        scheduler.Update(ReminderPause.Locked);
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.Phase == ReminderPhase.Work && scheduler.Remaining.TotalSeconds == 5 && scheduler.CompletedShorts == 0,
            "sleep through a due break yields fresh work without backlog or cadence credit");

        Step(scheduler, clock, 16);
        scheduler.Update(ReminderPause.Presentation);
        Step(scheduler, clock, 1, ReminderPause.Presentation);
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.Phase == ReminderPhase.Warning && scheduler.Remaining.TotalSeconds == 10,
            "short interruption re-arms a full warning before a partially used break resumes");
        Step(scheduler, clock, 10);
        Expect(scheduler.Phase == ReminderPhase.Break && scheduler.Remaining.TotalSeconds == 2,
            "a re-armed break retains its remaining duration");
        clock.Advance(TimeSpan.FromHours(3));
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.Phase == ReminderPhase.Work && scheduler.CompletedShorts == 0, "an unobserved suspend gap does not complete a break");
    }

    private static void ClockAndHours()
    {
        var clock = new TestClock(Noon);
        var scheduler = new ReminderScheduler(clock, ShortCycle);
        Step(scheduler, clock, 2);
        clock.ShiftWall(TimeSpan.FromHours(-8));
        scheduler.Update(ReminderPause.None);
        clock.ShiftWall(TimeSpan.FromHours(35));
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.Remaining.TotalSeconds == 3, "manual wall changes do not change the monotonic countdown");

        var overnight = ShortCycle with { WorkingHours = true, StartMinute = 22 * 60, EndMinute = 6 * 60 };
        clock = new TestClock(new DateTimeOffset(2026, 1, 2, 23, 59, 58, TimeSpan.Zero));
        scheduler = new ReminderScheduler(clock, overnight);
        Step(scheduler, clock, 5);
        Expect(scheduler.Phase == ReminderPhase.Warning, "an overnight working window continues across midnight");
        clock.ShiftWall(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(47));
        Step(scheduler, clock, 10);
        Expect(scheduler.Phase == ReminderPhase.Work && scheduler.PausedBy == ReminderPause.OutsideHours,
            "closing hours at warning expiry prevents an overlay");
        clock.ShiftWall(TimeSpan.FromHours(16));
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.PausedBy == ReminderPause.None && scheduler.Remaining.TotalSeconds == 5, "the next opening starts a fresh interval");
    }

    private static void Restart()
    {
        var clock = new TestClock(Noon);
        var settings = ShortCycle with { WorkSeconds = 35, ShortSeconds = 30 };
        var scheduler = new ReminderScheduler(clock, settings);
        Step(scheduler, clock, 15);
        scheduler.SetManualPause(true);
        scheduler.Update(ReminderPause.None);
        Step(scheduler, clock, 20);
        var saved = scheduler.Checkpoint();
        clock.Advance(TimeSpan.FromSeconds(1));
        scheduler = new ReminderScheduler(clock, settings, saved);
        Step(scheduler, clock, 9);
        scheduler.SetManualPause(false);
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.Remaining.TotalSeconds == 35, "restart retains accrued manual pause credit");

        Step(scheduler, clock, 35);
        var interrupted = scheduler.Checkpoint();
        clock.Advance(TimeSpan.FromSeconds(1));
        scheduler = new ReminderScheduler(clock, settings, interrupted);
        Expect(scheduler.Phase == ReminderPhase.Work && scheduler.Remaining.TotalSeconds == 35,
            "restart never reopens an old warning or overlay");
        scheduler = new ReminderScheduler(clock, settings with { Enabled = false }, saved);
        Expect(scheduler.ManuallyPaused && scheduler.Phase == ReminderPhase.Work,
            "presentation pause survives restart even with scheduled reminders off");
    }

    private static void ExplicitWorkPause()
    {
        var clock = new TestClock(Noon);
        var settings = ShortCycle with { WorkSeconds = 30, WorkingHours = true };
        var scheduler = new ReminderScheduler(clock, settings);
        Step(scheduler, clock, 15);
        Expect(scheduler.SetWorkPause(true, ReminderPause.None) && scheduler.Remaining.TotalSeconds == 15,
            "work pause freezes the partially used work interval");
        scheduler.SetManualPause(true);
        clock.Advance(TimeSpan.FromHours(8));
        scheduler.Update(ReminderPause.Locked | ReminderPause.Suspended);
        var system = ReminderPause.Locked | ReminderPause.Suspended | ReminderPause.OutsideHours | ReminderPause.Manual;
        Expect(scheduler.PausedBy == (system | ReminderPause.Work) && scheduler.Remaining.TotalSeconds == 15,
            "work pause retains time through hours, presentation, lock and sleep");

        var saved = scheduler.Checkpoint();
        clock.Advance(TimeSpan.FromDays(2));
        scheduler = new ReminderScheduler(clock, settings, saved);
        scheduler.Update(ReminderPause.Locked | ReminderPause.Suspended);
        Expect(scheduler.WorkPaused && scheduler.SetWorkPause(false, ReminderPause.Locked | ReminderPause.Suspended) &&
            scheduler.PausedBy == system && scheduler.Remaining.TotalSeconds == 15,
            "restart restores frozen work, and resume clears only the work pause");

        clock = new TestClock(Noon);
        scheduler = new ReminderScheduler(clock, settings);
        Step(scheduler, clock, 30);
        Expect(!scheduler.SetWorkPause(true, ReminderPause.None), "the warning cannot be paused as work");
        scheduler.Snooze();
        Step(scheduler, clock, 22);
        scheduler.SetWorkPause(true, ReminderPause.None);
        saved = scheduler.Checkpoint() with { CompletedShorts = 3 };
        clock.Advance(TimeSpan.FromDays(3));
        var updated = settings with { WorkSeconds = 21 };
        scheduler = new ReminderScheduler(clock, settings, saved).WithSettings(updated);
        scheduler.Update(ReminderPause.None);
        Expect(scheduler.WorkPaused && scheduler.Phase == ReminderPhase.Snoozed && scheduler.Remaining.TotalSeconds == 278 &&
            scheduler.IsLongBreak && scheduler.Settings == updated,
            "restart and timing edits preserve the paused snooze and its cadence");
        scheduler.SetManualPause(true);
        var stopped = scheduler.WithSettings(updated with { Enabled = false });
        var started = stopped.WithSettings(updated);
        started.Update(ReminderPause.None);
        Expect(!stopped.WorkPaused && !started.WorkPaused && started.ManuallyPaused && started.Phase == ReminderPhase.Work &&
            started.Remaining.TotalSeconds == 21 && started.CompletedShorts == 0,
            "stop clears the work pause and start uses fresh configured work while retaining presentation pause");

        clock = new TestClock(Noon);
        scheduler = new ReminderScheduler(clock, settings);
        Step(scheduler, clock, 2);
        scheduler.SetWorkPause(true, ReminderPause.None);
        scheduler = scheduler.WithSettings(updated);
        scheduler.SetWorkPause(false, ReminderPause.None);
        saved = scheduler.Checkpoint();
        clock.Advance(TimeSpan.FromSeconds(1));
        scheduler = new ReminderScheduler(clock, updated, saved);
        Expect(scheduler.Settings == updated && scheduler.Remaining.TotalSeconds == 28 && !scheduler.WorkPaused,
            "a quick restart after resume preserves the current interval before using the newly configured duration");
    }

    private static void ConfigurableRhythm()
    {
        var clock = new TestClock(Noon);
        var settings = ShortCycle with { ShortsBeforeLong = 4, SnoozeSeconds = 73 };
        var scheduler = new ReminderScheduler(clock, settings);
        Step(scheduler, clock, 5);
        scheduler.Snooze();
        Expect(scheduler.Phase == ReminderPhase.Snoozed && scheduler.Remaining.TotalSeconds == 73,
            "a new snooze uses the configured exact seconds");
        Step(scheduler, clock, 10);
        scheduler.SetWorkPause(true, ReminderPause.None);
        var shorter = settings with { SnoozeSeconds = 1 };
        scheduler = scheduler.WithSettings(shorter);
        clock.Advance(TimeSpan.FromHours(2));
        scheduler = new ReminderScheduler(clock, shorter, scheduler.Checkpoint());
        scheduler.SetWorkPause(false, ReminderPause.None);
        var resumed = scheduler.Checkpoint();
        clock.Advance(TimeSpan.FromSeconds(1));
        scheduler = new ReminderScheduler(clock, shorter, resumed);
        Expect(scheduler.Phase == ReminderPhase.Snoozed && scheduler.Remaining.TotalSeconds == 63 && !scheduler.WorkPaused,
            "a shorter configured snooze leaves the currently paused then resumed snooze intact across restarts");
        Step(scheduler, clock, 63);
        scheduler.Snooze();
        Expect(scheduler.Remaining.TotalSeconds == 1, "the next snooze uses the updated one-second duration");
        Step(scheduler, clock, 11);
        var remaining = scheduler.Remaining;
        scheduler = scheduler.WithSettings(shorter with { SnoozeSeconds = 3600 });
        Expect(scheduler.Phase == ReminderPhase.Break && scheduler.Remaining == remaining,
            "a snooze-only edit leaves an active break intact");
        Step(scheduler, clock, 3);
        Step(scheduler, clock, 18);
        Expect(scheduler.CompletedShorts == 2 && !scheduler.IsLongBreak,
            "two short completions accumulate under a threshold of four");

        var lowered = settings with { ShortsBeforeLong = 1 };
        scheduler = scheduler.WithSettings(lowered);
        Expect(scheduler.CompletedShorts == 1 && scheduler.IsLongBreak && scheduler.Settings.BreakLength(1).TotalSeconds == 8,
            "lowering the threshold caps earned shorts and puts a long break next");
        Step(scheduler, clock, 15);
        scheduler.Skip();
        Expect(scheduler.Phase == ReminderPhase.Work && scheduler.IsLongBreak,
            "ending a configured long break early leaves it due");
        scheduler = scheduler.WithSettings(settings with { ShortsBeforeLong = 2 });
        Expect(scheduler.CompletedShorts == 1 && !scheduler.IsLongBreak,
            "raising the threshold retains earned shorts and waits for the balance");
        Step(scheduler, clock, 18);
        Step(scheduler, clock, 23);
        Expect(scheduler.CompletedShorts == 0 && !scheduler.IsLongBreak,
            "naturally completing a custom long break resets earned shorts");

        Step(scheduler, clock, 18);
        scheduler = scheduler.WithSettings(settings with { LongBreaks = false, ShortsBeforeLong = 12 });
        Step(scheduler, clock, 18 * 13);
        Expect(scheduler.Phase == ReminderPhase.Work && scheduler.CompletedShorts == 0 && !scheduler.IsLongBreak,
            "disabling long breaks clears cadence and never accumulates long-break credit");
        scheduler = scheduler.WithSettings(settings with { ShortsBeforeLong = 12 });
        Step(scheduler, clock, 18 * 12);
        Expect(scheduler.CompletedShorts == 12 && scheduler.IsLongBreak,
            "the maximum threshold works after re-enabling long breaks from zero");
        Expect(ReminderDuration.Format(73) == "1 min 13 sec" && ReminderDuration.Format(1) == "1 sec" &&
            ReminderDuration.Format(3600) == "60 min", "duration labels preserve odd seconds and exact whole minutes");
    }

    private static void Step(ReminderScheduler scheduler, TestClock clock, int seconds, ReminderPause reasons = ReminderPause.None)
    {
        for (var index = 0; index < seconds; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            scheduler.Update(reasons);
        }
    }

    private static void Expect(bool passed, string name)
    {
        if (!passed)
        {
            throw new InvalidOperationException(name);
        }
        Console.WriteLine($"PASS {name}");
    }
}
