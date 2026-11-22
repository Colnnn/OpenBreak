using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;

namespace Afterbreak;

internal static class BreakDisplayChecks
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    internal static void Run(string folder)
    {
        CheckManual(Path.Combine(folder, "manual-break-views.db"));
        CheckScheduled(Path.Combine(folder, "scheduled-break-views.db"));
        CheckInvalidSettings(Path.Combine(folder, "invalid-display.db"));
    }

    private static void CheckManual(string path)
    {
        var store = new TaskStore(path);
        store.Add("First To do", "A next step.");
        store.Add("Second To do");
        store.Add("Working task");
        store.Add("Completed task");
        store.Add("Archived task", "Do not show.");
        var seeded = store.List(archived: false);
        store.Move(seeded[2].Id, TaskStatus.InProgress);
        store.Move(seeded[3].Id, TaskStatus.Done);
        store.SetArchived(seeded[4].Id, archived: true);
        var (session, queue, tray) = Open(store, new TestClock(Start));

        tray.Send(TrayCommand.Preview);
        store.Move(seeded[1].Id, TaskStatus.ToDo, beforeId: seeded[0].Id);
        store.Edit(seeded[2].Id, "Latest working title", "An edit after the warning.");
        store.Add("Captured during warning");
        var snapshot = store.List(archived: false);
        ExpireWarning(session, queue);
        Check(BreakWindow.Layout == BreakLayout.SavedBoard && BreakWindow.SavedTasks?.SequenceEqual(snapshot) == true &&
            BreakWindow.Text is null && snapshot[0].Id == seeded[1].Id && snapshot[^1].Status == TaskStatus.Done,
            "standard preview defaults to the newest saved board, ordered across all active lanes");
        tray.Send(TrayCommand.EndBreak);

        tray.Send(TrayCommand.PreviewSampleBoard);
        WhileDatabaseIsAbsent(path, () => ExpireWarning(session, queue));
        Check(BreakWindow.Layout == BreakLayout.SampleBoard && BreakWindow.SavedTasks is null && BreakWindow.Text is null &&
            store.List(archived: false).SequenceEqual(snapshot) && store.List(archived: true).Single().Id == seeded[4].Id,
            "sample preview never opens the task database or changes saved rows");
        tray.Send(TrayCommand.EndBreak);

        tray.Send(TrayCommand.Preview);
        ExecuteSql(path, "ALTER TABLE tasks RENAME TO preserved_tasks;");
        try
        {
            ExpireWarning(session, queue);
            Check(BreakWindow.Layout == BreakLayout.TimerOnly && BreakWindow.SavedTasks is null && BreakWindow.Text is null,
                "a saved-board read failure falls back to a clean timer-only view");
        }
        finally
        {
            ExecuteSql(path, "ALTER TABLE preserved_tasks RENAME TO tasks;");
        }
        tray.Send(TrayCommand.EndBreak);

        tray.Send(TrayCommand.Settings);
        var settings = SettingsWindow.Current;
        Check(settings.LoadedSettings is { BreakDisplay: BreakDisplay.SavedTaskBoard } configured &&
            settings.Save(configured with { BreakDisplay = BreakDisplay.OneTask }), "settings switch the view to One task");
        settings.Hide();
        tray.Send(TrayCommand.Preview);
        ExpireWarning(session, queue);
        Check(BreakWindow.Layout == BreakLayout.Compact && BreakWindow.SavedTasks is null &&
            BreakWindow.Text == "Latest working title" && BreakWindow.Note == "An edit after the warning.",
            "One task uses the first In progress card and its latest note");
        tray.Send(TrayCommand.EndBreak);

        tray.Send(TrayCommand.Settings);
        Check(settings.Save(settings.LoadedSettings! with { BreakDisplay = BreakDisplay.TimerOnly }),
            "settings persist Timer only");
        tray.Send(TrayCommand.Quit);
        (session, queue, tray) = Open(new TaskStore(path), new TestClock(Start));
        tray.Send(TrayCommand.Preview);
        WhileDatabaseIsAbsent(path, () => ExpireWarning(session, queue));
        Check(BreakWindow.Layout == BreakLayout.TimerOnly && BreakWindow.SavedTasks is null && BreakWindow.Text is null &&
            BreakWindow.Note is null && store.LoadReminders().Settings.BreakDisplay == BreakDisplay.TimerOnly,
            "Timer only survives restart and never opens the task database for a preview");
        tray.Send(TrayCommand.EndBreak);

        tray.Send(TrayCommand.Settings);
        settings = SettingsWindow.Current;
        Check(settings.Save(settings.LoadedSettings! with { BreakDisplay = BreakDisplay.SavedTaskBoard }),
            "settings can restore the default board view");
        settings.Hide();
        foreach (var task in snapshot)
        {
            store.SetArchived(task.Id, archived: true);
        }
        tray.Send(TrayCommand.Preview);
        ExpireWarning(session, queue);
        Check(BreakWindow.Layout == BreakLayout.TimerOnly && BreakWindow.SavedTasks is null &&
            store.List(archived: true).Count == snapshot.Count + 1, "an empty active board leaves only the timer");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckScheduled(string path)
    {
        var store = new TaskStore(path);
        store.Add("Scheduled card");
        var card = store.List(archived: false).Single();
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5 };
        store.SaveReminders(settings, new ReminderCheckpoint(ReminderPhase.Work, settings.WorkLength, 2, false, TimeSpan.Zero, Start));
        var clock = new TestClock(Start);
        var (_, queue, tray) = Open(store, clock);
        var timer = queue.Timers[1];

        void Tick(int seconds)
        {
            for (var index = 0; index < seconds; index++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                timer.Fire();
            }
        }

        Tick(5);
        Check(WarningWindow.Current.IsVisible, "a scheduled warning starts before the board snapshot");
        store.Move(card.Id, TaskStatus.InProgress);
        store.Edit(card.Id, "Scheduled latest title", "Changed during the warning.");
        Tick(10);
        Check(BreakWindow.Layout == BreakLayout.SavedBoard && BreakWindow.SavedTasks?.SingleOrDefault() is
            { Title: "Scheduled latest title", NextStep: "Changed during the warning.", Status: TaskStatus.InProgress },
            "scheduled board also reads the latest saved tasks at appearance");
        Tick(3);
        Check(BreakWindow.Current.Seconds == 27, "the scheduler reaches a partial short break");
        tray.Send(TrayCommand.Settings);
        var form = SettingsWindow.Current;
        Check(form.Save(settings with { BreakDisplay = BreakDisplay.OneTask }) && !BreakWindow.Current.IsVisible &&
            store.LoadReminders().Checkpoint is { CompletedShorts: 2, Phase: ReminderPhase.Warning },
            "a display-only save during Settings preserves cadence and defers the interrupted break");
        form.Hide();
        timer.Fire();
        Tick(10);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 27 &&
            BreakWindow.Layout == BreakLayout.Compact && BreakWindow.Text == "Scheduled latest title",
            "resuming after Settings uses the new view and the exact remaining break time");
        Tick(27);
        Check(store.LoadReminders().Checkpoint?.CompletedShorts == 3, "completing the resumed break advances cadence once");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckInvalidSettings(string path)
    {
        var store = new TaskStore(path);
        store.SaveReminders(ReminderSettings.Default, new ReminderScheduler(new TestClock(Start), ReminderSettings.Default).Checkpoint());
        ExecuteSql(path, """UPDATE reminder_state SET settings = replace(settings, '"BreakDisplay":0', '"BreakDisplay":9');""");
        var (_, _, tray) = Open(store, new TestClock(Start));
        var before = File.ReadAllBytes(path);
        tray.Send(TrayCommand.Settings);
        Check(!SettingsWindow.Current.Save(ReminderSettings.Default with { BreakDisplay = BreakDisplay.TimerOnly }) &&
            File.ReadAllBytes(path).SequenceEqual(before), "a corrupt display setting cannot be replaced by saving fallback settings");
        tray.Send(TrayCommand.Quit);
    }

    private static (AfterbreakSession Session, DispatcherQueue Queue, Desktop Tray) Open(TaskStore store, TestClock clock)
    {
        var queue = new DispatcherQueue();
        var session = new AfterbreakSession(queue, () => { }, store, clock);
        return (session, queue, Desktop.Current);
    }

    private static void ExpireWarning(AfterbreakSession session, DispatcherQueue queue)
    {
        var startedAt = typeof(AfterbreakSession).GetField("startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        startedAt.SetValue(session, Stopwatch.GetTimestamp() - 11 * Stopwatch.Frequency);
        queue.Timer.Fire();
    }

    private static void WhileDatabaseIsAbsent(string path, Action action)
    {
        var retained = path + ".retained";
        File.Move(path, retained);
        try
        {
            action();
            Check(!File.Exists(path), "content-free preview leaves the absent database untouched");
        }
        finally
        {
            if (!File.Exists(path))
            {
                File.Move(retained, path);
            }
        }
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Check(bool passed, string description)
    {
        if (!passed)
        {
            throw new InvalidOperationException(description);
        }
        Console.WriteLine($"PASS {description}");
    }
}
