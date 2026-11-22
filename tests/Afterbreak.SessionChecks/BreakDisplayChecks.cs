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
        CheckGuidePreview(Path.Combine(folder, "guide-preview.db"));
        CheckGuideDuringSchedule(Path.Combine(folder, "guide-schedule.db"));
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

    private static void CheckGuidePreview(string path)
    {
        var store = new TaskStore(path);
        store.Add("Private task", "Private note");
        var tasks = store.List(archived: false);
        var (session, queue, tray) = Open(store, new TestClock(Start));
        tray.Send(TrayCommand.CaptureSettings, 101);
        var guide = CaptureSettingsWindow.Current;
        Check(guide.Display == BreakDisplay.SavedTaskBoard && store.LoadReminders().Checkpoint is null,
            "first Getting started shows the default break display without writing settings");

        tray.Send(TrayCommand.CaptureSettings, 102);
        WarningWindow.Current.CanPlace = false;
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && guide.IsVisible &&
            store.LoadReminders().Checkpoint is null && !WarningWindow.Current.IsVisible,
            "guide warning placement failure keeps the guide and leaves the choice unsaved");
        WarningWindow.Current.CanPlace = true;

        tray.SystemPause = ReminderPause.Locked;
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && store.LoadReminders().Checkpoint is null,
            "a locked session blocks guide saving and preview");
        tray.SystemPause = ReminderPause.None;

        tray.Send(TrayCommand.Settings);
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && store.LoadReminders().Checkpoint is null,
            "the guide cannot overwrite a choice while Reminder settings has an open draft");
        SettingsWindow.Current.Hide();

        tray.Send(TrayCommand.Capture);
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && CaptureWindow.Current.IsVisible,
            "guide preview cannot close an active capture");
        CaptureWindow.Current.Cancel();
        tray.Send(TrayCommand.Preview);
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && WarningWindow.Current.IsVisible &&
            store.LoadReminders().Checkpoint is null, "guide preview cannot replace a current preview");
        tray.Send(TrayCommand.EndBreak);

        ExecuteSql(path, """
            CREATE TRIGGER ignore_guide_insert BEFORE INSERT ON reminder_state BEGIN SELECT RAISE(IGNORE); END;
            """);
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && guide.IsVisible &&
            !WarningWindow.Current.IsVisible && store.LoadReminders().Checkpoint is null,
            "a suppressed first guide write reports failure and starts no warning");
        ExecuteSql(path, "DROP TRIGGER ignore_guide_insert;");

        var pauseChecks = 0;
        tray.CheckSystemPause = () =>
        {
            if (++pauseChecks == 2)
            {
                guide.Foreground = false;
            }
            return ReminderPause.None;
        };
        var returns = SurfaceWindow.Returns;
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && guide.IsVisible &&
            !WarningWindow.Current.IsVisible && SurfaceWindow.Returns == returns &&
            store.LoadReminders().Settings == ReminderSettings.Default with { BreakDisplay = BreakDisplay.TimerOnly },
            "loss of guide foreground after saving keeps the draft and never restores or previews");
        tray.CheckSystemPause = null;
        guide.Foreground = true;

        WarningWindow.Current.CanShowPrepared = false;
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && guide.IsVisible &&
            !WarningWindow.Current.IsVisible && SurfaceWindow.Returns == returns && tray.PreviewAvailable,
            "a warning that fails to open leaves the guide available for retry");
        WarningWindow.Current.CanShowPrepared = true;

        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is null && !guide.IsVisible &&
            WarningWindow.Current.IsVisible && WarningWindow.Seconds == 10 && WarningWindow.Current.LastSource == 101 &&
            SurfaceWindow.Returned == 101 && SurfaceWindow.Returns == returns + 1 && tray.DismissLabel == "Cancel preview",
            "guide success starts the normal warning and returns to the original opening source");
        var shows = WarningWindow.Shows;
        Check(guide.SaveAndPreview(BreakDisplay.OneTask) is not null && WarningWindow.Shows == shows &&
            store.LoadReminders().Settings.BreakDisplay == BreakDisplay.TimerOnly,
            "a stale guide click while hidden cannot write or start another preview");

        tray.WorkSource = 202;
        WhileDatabaseIsAbsent(path, () => ExpireWarning(session, queue));
        Check(BreakWindow.Layout == BreakLayout.TimerOnly && BreakWindow.Text is null && BreakWindow.SavedTasks is null,
            "the guide Timer only preview bypasses task reads");
        tray.Send(TrayCommand.EndBreak);
        Check(SurfaceWindow.Returned == 202 && store.List(archived: false).SequenceEqual(tasks) &&
            store.LoadCapturePreferences() == new CapturePreferences(CaptureShortcut.Default, false),
            "the preview returns to the latest work host and leaves tasks, shortcut and guide acknowledgement alone");
        tray.Send(TrayCommand.Quit);

        (_, queue, tray) = Open(new TaskStore(path), new TestClock(Start));
        tray.Send(TrayCommand.CaptureSettings, 303);
        Check(CaptureSettingsWindow.Current.Display == BreakDisplay.TimerOnly && !queue.Timers[1].Running,
            "Getting started loads the saved choice after restart while reminders remain off");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckGuideDuringSchedule(string path)
    {
        var store = new TaskStore(path);
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5 };
        store.SaveReminders(settings, new ReminderCheckpoint(ReminderPhase.Work, settings.WorkLength, 2, false, TimeSpan.Zero, Start));
        var clock = new TestClock(Start);
        var (session, queue, tray) = Open(store, clock);
        var timer = queue.Timers[1];

        void Tick(int seconds)
        {
            for (var index = 0; index < seconds; index++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                timer.Fire();
            }
        }

        Tick(18);
        Check(BreakWindow.Current.Seconds == 27, "guide fixture reaches a partial scheduled break");
        tray.Send(TrayCommand.CaptureSettings);
        var guide = CaptureSettingsWindow.Current;
        var before = store.LoadReminders().Checkpoint;
        ExecuteSql(path, """
            CREATE TRIGGER ignore_guide_write BEFORE UPDATE ON reminder_state BEGIN SELECT RAISE(IGNORE); END;
            """);
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is not null && guide.IsVisible && timer.Running &&
            store.LoadReminders() == (settings, before), "a failed guide save leaves the live schedule and durable reminder state alone");
        ExecuteSql(path, "DROP TRIGGER ignore_guide_write;");
        Check(guide.SaveAndPreview(BreakDisplay.TimerOnly) is null &&
            store.LoadReminders() == (settings with { BreakDisplay = BreakDisplay.TimerOnly }, before),
            "guide success persists only the display and leaves the checkpoint untouched");
        ExpireWarning(session, queue);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 10,
            "an enabled schedule still gets a ten-second manual guide preview");
        tray.Send(TrayCommand.EndBreak);
        timer.Fire();
        Tick(10);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 27 &&
            store.LoadReminders().Checkpoint?.CompletedShorts == 2,
            "the guide preview earns no cadence credit and the interrupted break resumes with its remaining time");
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
