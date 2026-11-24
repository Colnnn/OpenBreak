namespace OpenBreak;

internal static class WarningInputChecks
{
    internal static void Run()
    {
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5, ShortSeconds = 10 };
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var scheduler = new ReminderScheduler(clock, settings);
        int reads = 0;
        uint input = 0;
        uint NowTick() => 100_000 + (uint)(clock.GetTimestamp() / TimeSpan.TicksPerMillisecond);
        LastInputSample? Read()
        {
            reads++;
            return new LastInputSample(NowTick(), input);
        }
        void Tick(int milliseconds, ReminderPause pause = ReminderPause.None)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            scheduler.Update(pause, Read);
        }

        Tick(5000);
        Tick(5000);
        input = NowTick();
        Tick(5000);
        Check(scheduler.Phase == ReminderPhase.Break && reads == 0, "Disabled input hold keeps the original warning and never queries activity");

        scheduler.Skip();
        Tick(2000);
        scheduler = scheduler.WithSettings(settings with { FinishTyping = true });
        Check(scheduler.Remaining == TimeSpan.FromSeconds(3), "Turning on input hold preserves current work time");
        Tick(3000);
        Tick(5000);
        Tick(3900);
        Check(reads == 0 && scheduler.Remaining == TimeSpan.FromMilliseconds(1100), "Sampling stays dormant before the scheduled final second");

        input = NowTick();
        Tick(1200);
        Check(scheduler is { Phase: ReminderPhase.Warning, HoldingForInput: true, PausedBy: ReminderPause.None } &&
            scheduler.Remaining == TimeSpan.FromSeconds(1) && reads == 1,
            "A delayed tick crossing zero holds the warning at one second without pausing the scheduler");
        var snooze = scheduler.Copy();
        snooze.Snooze();
        Check(snooze is { Phase: ReminderPhase.Snoozed, HoldingForInput: false } && scheduler.HoldingForInput,
            "A snooze candidate clears its hold without changing the original warning");
        Tick(1800);
        Check(!scheduler.HoldingForInput && scheduler.Remaining == TimeSpan.FromSeconds(1), "Release leaves a full second after the quiet period");
        Tick(500);
        Check(scheduler.Remaining == TimeSpan.FromMilliseconds(500), "Only post-release elapsed time counts");
        input = NowTick();
        Tick(500);
        Check(scheduler.HoldingForInput && scheduler.Remaining == TimeSpan.FromSeconds(1), "New activity can hold the final second again");

        var disabled = scheduler.WithSettings(settings);
        Check(!disabled.HoldingForInput && disabled.Phase == ReminderPhase.Warning &&
            disabled.Remaining == TimeSpan.FromSeconds(1) && scheduler.HoldingForInput,
            "Turning Finish typing off during a hold releases a full second without restarting the warning");
        var restored = new ReminderScheduler(clock, scheduler.Settings, scheduler.Checkpoint());
        Check(!restored.HoldingForInput && restored.Phase == ReminderPhase.Work,
            "Restoring a checkpoint cannot resume an input hold");

        var priorReads = reads;
        Tick(250, ReminderPause.Locked | ReminderPause.Interaction);
        Tick(250, ReminderPause.Interaction);
        Check(!scheduler.HoldingForInput && scheduler.PausedBy == ReminderPause.Interaction && reads == priorReads &&
            scheduler.Remaining == ReminderScheduler.WarningLength, "Lock and capture clear the hold and retain existing overlap precedence");
        Tick(250);
        Tick(5000);
        input = NowTick();
        Tick(4000);
        Check(!scheduler.HoldingForInput && scheduler.Remaining == TimeSpan.FromSeconds(1),
            "A fresh warning does not treat the first old input timestamp as new activity");

        input = NowTick();
        Tick(250);
        Check(scheduler.HoldingForInput, "The restarted warning can hold on fresh input");
        Tick(6000);
        Check(!scheduler.HoldingForInput && scheduler.Remaining == ReminderScheduler.WarningLength,
            "An elapsed sleep gap restarts the warning without stale input state");
        scheduler.Skip();
        Check(scheduler is { Phase: ReminderPhase.Work, HoldingForInput: false, CompletedShorts: 0 }, "Skip clears input hold and keeps cadence");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
        Console.WriteLine($"PASS {message}");
    }
}
