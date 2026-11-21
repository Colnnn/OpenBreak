using Microsoft.UI.Dispatching;

namespace Afterbreak;

internal sealed class SimulatedReminderDay(string path)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 9, 5, 0, TimeSpan.Zero);
    private readonly TestClock clock = new(Start);
    private readonly TaskStore store = new(path);
    private DispatcherQueueTimer scheduler = null!;
    private static Desktop Tray => Desktop.Current;

    internal void Run()
    {
        SeedTasks();
        var initialCards = store.List(archived: false).ToArray();
        var archivedCards = store.List(archived: true).ToArray();
        var returnTask = initialCards.First(task => task.Status == TaskStatus.InProgress);
        var settings = ReminderSettings.Default with { Enabled = true, WorkingHours = true };
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var openingCounts = ShowCount();
        StartSession();

        QuietFor(TimeSpan.FromSeconds(29));
        Require(Checkpoint().SavedAt == Start, "ordinary work does not checkpoint before 30 seconds");
        QuietFor(TimeSpan.FromSeconds(1));
        var periodic = Checkpoint();
        Require(periodic.Phase == ReminderPhase.Work && periodic.SavedAt == Start.AddSeconds(30) &&
            periodic.Remaining == TimeSpan.FromMinutes(29.5), "30 seconds of work are saved without a phase change or Quit");
        QuietFor(TimeSpan.FromSeconds(12));
        scheduler.Stop(); // Simulate the process ending before its next write, without sending Quit.
        clock.Advance(TimeSpan.FromSeconds(4));
        StartSession();
        ExpectWarningAfter(TimeSpan.FromMinutes(29.5));
        Tray.Send(TrayCommand.Snooze);
        QuietFor(TimeSpan.FromMinutes(2));
        QuitSession();
        clock.Advance(TimeSpan.FromSeconds(4));
        StartSession();
        ExpectWarningAfter(TimeSpan.FromMinutes(3));
        Tray.Send(TrayCommand.Skip);
        Require(Checkpoint().CompletedShorts == 0, "snoozing across restart and skipping leave cadence at zero");

        for (var count = 1; count <= 3; count++)
        {
            ExpectWarningAfter(TimeSpan.FromMinutes(30));
            ExpectBreak(returnTask, seconds: 30);
            FinishBreakAfter(TimeSpan.FromSeconds(30));
            Require(Checkpoint().CompletedShorts == count, $"natural short break {count} is counted once");
        }

        ExpectWarningAfter(TimeSpan.FromMinutes(30), longBreak: true);
        ExpectBreak(returnTask, seconds: 300, longBreak: true);
        Advance(TimeSpan.FromSeconds(10));
        Tray.Send(TrayCommand.EndBreak);
        Require(!BreakWindow.Current.IsVisible && Checkpoint().CompletedShorts == 3, "ending early leaves the long break due");

        Tray.Send(TrayCommand.Capture);
        Require(CaptureWindow.Current.Save("Afternoon follow-up"), "capture saves while the schedule is running");
        var captured = store.List(archived: false).Single(task => task.Title == "Afternoon follow-up");
        var expectedCards = initialCards.Where(task => task.Status == TaskStatus.ToDo)
            .Append(captured)
            .Concat(initialCards.Where(task => task.Status != TaskStatus.ToDo)).ToArray();
        Require(captured.Status == TaskStatus.ToDo, "capture appends a new To do task");
        ExpectWarningAfter(TimeSpan.FromMinutes(30), longBreak: true);
        ExpectBreak(returnTask, seconds: 300, longBreak: true);
        FinishBreakAfter(TimeSpan.FromMinutes(5));
        Require(Checkpoint().CompletedShorts == 0, "the naturally completed long break resets cadence");

        QuietFor(TimeSpan.FromMinutes(15));
        Tray.Send(TrayCommand.TogglePause);
        QuietFor(TimeSpan.FromSeconds(10));
        SetSystemPause(ReminderPause.Locked);
        QuietFor(TimeSpan.FromSeconds(10));
        QuitSession();
        clock.Advance(TimeSpan.FromSeconds(4));
        StartSession();
        Require(Tray.ManuallyPaused, "presentation pause survives restart while the session is locked");
        SetSystemPause(ReminderPause.Locked);
        QuietFor(TimeSpan.FromSeconds(3));
        Tray.Send(TrayCommand.TogglePause);
        QuietFor(TimeSpan.FromSeconds(3));
        SetSystemPause(ReminderPause.None);
        ExpectWarningAfter(TimeSpan.FromMinutes(30));
        ExpectBreak(returnTask, seconds: 30);
        FinishBreakAfter(TimeSpan.FromSeconds(30));

        QuietFor(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(5));
        SetSystemPause(ReminderPause.Suspended);
        var prior = ShowCount();
        clock.Advance(TimeSpan.FromMinutes(15));
        SetSystemPause(ReminderPause.Locked);
        QuietFor(TimeSpan.FromSeconds(5));
        SetSystemPause(ReminderPause.None);
        Require(ShowCount() == prior && Checkpoint().CompletedShorts == 1,
            "sleep through the due time creates no stale prompt or cadence credit");
        ExpectWarningAfter(TimeSpan.FromMinutes(30));
        ExpectBreak(returnTask, seconds: 30);
        FinishBreakAfter(TimeSpan.FromSeconds(30));

        // Move the end of working hours while the schedule is already running.
        var editTime = Start.Date.AddHours(14).AddMinutes(29).AddSeconds(49);
        QuietFor(new DateTimeOffset(editTime, TimeSpan.Zero) - clock.GetUtcNow());
        Tray.Send(TrayCommand.Settings);
        settings = settings with { EndMinute = 15 * 60 };
        Require(SettingsWindow.Current.Save(settings), "working-hours change saves");
        SettingsWindow.Current.Hide();
        scheduler.Fire();
        ExpectWarningAfter(TimeSpan.FromMinutes(30));
        QuietFor(TimeSpan.FromSeconds(9));
        var beforeClosing = ShowCount();
        var eligibilityReads = 0;
        Tray.CheckSystemPause = () =>
        {
            if (++eligibilityReads == 2)
            {
                // Simulate closing time passing during checkpoint persistence or task lookup.
                clock.ShiftWall(TimeSpan.FromSeconds(1));
            }
            return ReminderPause.None;
        };
        Advance(TimeSpan.FromSeconds(1));
        Tray.CheckSystemPause = null;
        Require(ShowCount() == beforeClosing && !BreakWindow.Current.IsVisible && !WarningWindow.Current.IsVisible,
            "closing hours during the final overlay check suppresses the stale break");

        QuietFor(TimeSpan.FromHours(1));
        QuitSession();
        Require(store.LoadReminders().Settings == settings && Checkpoint().CompletedShorts == 2,
            "daytime settings and previous completions survive closing hours");
        var nextMorning = Start.AddDays(1).AddMinutes(-10);
        clock.Advance(nextMorning - clock.GetUtcNow());
        StartSession();
        QuietFor(TimeSpan.FromMinutes(10));
        QuitSession();
        Require(clock.GetUtcNow() - Start == TimeSpan.FromHours(24) && Checkpoint().Remaining == TimeSpan.FromMinutes(25),
            "the next opening starts fresh work with no reminder backlog over a simulated 24 hours");
        var reopened = new TaskStore(path);
        Require(reopened.List(archived: false).SequenceEqual(expectedCards) &&
            reopened.List(archived: true).SequenceEqual(archivedCards) && reopened.ReturnTask() == returnTask,
            "all active and archived records, notes, lane order and return task survive the simulated day");
        Require(ShowCount() == (openingCounts.Warnings + 10, openingCounts.Breaks + 7),
            "the complete day displays the ten expected warnings and seven expected overlays");
        Console.WriteLine("PASS 24-hour simulated reminder day with SQLite checkpoints, restarts and preserved task data");
    }

    private void SeedTasks()
    {
        foreach (var title in new[] { "Quote review", "Handover", "Drawing list", "Site notes", "Sent package", "Past work" })
        {
            store.Add(title);
        }
        var cards = store.List(archived: false);
        store.Move(cards[0].Id, TaskStatus.InProgress);
        store.Move(cards[2].Id, TaskStatus.InProgress, beforeId: cards[0].Id);
        store.Edit(cards[2].Id, cards[2].Title, "Check sheet three");
        store.MoveWithin(cards[3].Id, -1);
        store.Move(cards[4].Id, TaskStatus.Done);
        store.SetArchived(cards[5].Id, archived: true);
    }

    private void StartSession()
    {
        var queue = new DispatcherQueue();
        var before = ShowCount();
        _ = new AfterbreakSession(queue, () => { }, new TaskStore(path), clock);
        scheduler = queue.Timers[1];
        Require(scheduler.Running && ShowCount() == before, "restart resumes quietly");
    }

    private void QuitSession()
    {
        Tray.Send(TrayCommand.Quit);
        Require(!scheduler.Running && Tray.Disposed, "quiet shutdown stops scheduled ticks");
    }

    private void SetSystemPause(ReminderPause pauses)
    {
        Tray.SystemPause = pauses;
        Tray.ChangeEligibility();
    }

    private void ExpectWarningAfter(TimeSpan delay, bool longBreak = false)
    {
        QuietFor(delay - TimeSpan.FromSeconds(1));
        var before = ShowCount();
        Advance(TimeSpan.FromSeconds(1));
        var kind = longBreak ? "Long break" : "Short break";
        Require(ShowCount() == (before.Warnings + 1, before.Breaks) && WarningWindow.Current.IsVisible &&
            WarningWindow.Seconds == 10 && WarningWindow.Kind == kind, $"{kind} warning begins on time with ten seconds remaining");
    }

    private void ExpectBreak(BoardTask returnTask, int seconds, bool longBreak = false)
    {
        QuietFor(TimeSpan.FromSeconds(9));
        var before = ShowCount();
        Advance(TimeSpan.FromSeconds(1));
        Require(ShowCount() == (before.Warnings, before.Breaks + 1) && !WarningWindow.Current.IsVisible &&
            BreakWindow.Current.IsVisible && BreakWindow.Current.IsLong == longBreak && BreakWindow.Current.Seconds == seconds &&
            BreakWindow.Text == returnTask.Title && BreakWindow.Note == returnTask.NextStep,
            "scheduled break opens once with the right duration and the ordered return task");
    }

    private void FinishBreakAfter(TimeSpan duration)
    {
        QuietFor(duration - TimeSpan.FromSeconds(1));
        Require(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 1, "break stays open through the last second");
        QuietFor(TimeSpan.FromSeconds(1));
        Require(!BreakWindow.Current.IsVisible, "break closes once at natural expiry");
    }

    private void QuietFor(TimeSpan duration)
    {
        var before = ShowCount();
        Advance(duration);
        Require(ShowCount() == before, $"no new warning or break during the preceding {duration}");
    }

    private void Advance(TimeSpan duration)
    {
        Require(duration >= TimeSpan.Zero && duration.Ticks % TimeSpan.TicksPerSecond == 0, "scenario uses positive whole-second steps");
        for (var step = TimeSpan.Zero; step < duration; step += TimeSpan.FromSeconds(1))
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            scheduler.Fire();
        }
    }

    private ReminderCheckpoint Checkpoint() => store.LoadReminders().Checkpoint ?? throw new InvalidOperationException("Missing reminder checkpoint.");

    private static (int Warnings, int Breaks) ShowCount() => (WarningWindow.Shows, BreakWindow.Shows);

    private void Require(bool passed, string explanation)
    {
        if (!passed)
        {
            throw new InvalidOperationException($"Simulated day at {clock.GetUtcNow():O} with {ShowCount()}: {explanation}.");
        }
    }
}
