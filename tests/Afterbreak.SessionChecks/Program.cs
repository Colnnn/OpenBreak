using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;

namespace Afterbreak;

internal static class Program
{
    private static void Main()
    {
        var temporary = Directory.CreateTempSubdirectory("Afterbreak-session-");
        try
        {
            CheckSession(Path.Combine(temporary.FullName, "tasks.db"));
            CheckIgnoredCaptureInsert(Path.Combine(temporary.FullName, "ignored-capture.db"));
            BreakDisplayChecks.Run(temporary.FullName);
            CheckTrayMenuPolicy();
            CheckCaptureMotionStudy(Path.Combine(temporary.FullName, "motion-study.db"));
            CheckReminderInterlock(Path.Combine(temporary.FullName, "reminders.db"));
            CheckFailedStartupRead(Path.Combine(temporary.FullName, "startup.db"));
            CheckCaptureSettings(Path.Combine(temporary.FullName, "capture-settings.db"));
            CheckAppearanceSettings(Path.Combine(temporary.FullName, "appearance.db"));
            PlacementLifecycleChecks.Run(temporary.FullName);
            new SimulatedReminderDay(Path.Combine(temporary.FullName, "day.db")).Run();
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static void CheckIgnoredCaptureInsert(string path)
    {
        var store = new TaskStore(path);
        store.Add("Existing task");
        var before = store.List(archived: false);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER ignore_capture BEFORE INSERT ON tasks
            BEGIN
                UPDATE tasks SET title = 'Unexpected trigger change';
                SELECT RAISE(IGNORE);
            END;
            """;
        trigger.ExecuteNonQuery();

        _ = new AfterbreakSession(new DispatcherQueue(), () => { }, store);
        var tray = Desktop.Current;
        tray.Send(TrayCommand.Capture, 701);
        var capture = CaptureWindow.Current;
        capture.Input = "Retain the title";
        capture.NextStep = "Retain the next step";
        var returns = SurfaceWindow.Returns;

        Check(!capture.Save(capture.Input, capture.NextStep) && capture.IsVisible &&
            capture.Input == "Retain the title" && capture.NextStep == "Retain the next step" &&
            store.List(archived: false).SequenceEqual(before) && SurfaceWindow.Returns == returns,
            "an ignored SQLite insert keeps both draft fields and rolls back task changes");

        trigger.CommandText = "DROP TRIGGER ignore_capture;";
        trigger.ExecuteNonQuery();
        Check(capture.Save(capture.Input, capture.NextStep) && !capture.IsVisible &&
            !capture.Save(capture.Input, capture.NextStep) && SurfaceWindow.Returned == 701 &&
            SurfaceWindow.Returns == returns + 1 &&
            new TaskStore(path).List(archived: false) is [var existing,
                { Title: "Retain the title", NextStep: "Retain the next step" }] && existing == before.Single(),
            "retry saves the retained title and next step once and returns to the original host");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckTrayMenuPolicy()
    {
        var state = new TrayMenuState("Ctrl + Alt + K", false, null, "Reminders off", false, false, false);
        var items = state.Items();
        Check(items.Where(item => item.Visible).Select(item => item.Command).SequenceEqual(
            [TrayCommand.Tasks, TrayCommand.Capture, TrayCommand.CaptureSettings, TrayCommand.Appearance,
                TrayCommand.Preview, TrayCommand.PreviewSampleBoard, TrayCommand.Settings, TrayCommand.TogglePause, TrayCommand.Quit]) &&
            items.Single(item => item.Command == TrayCommand.Capture).Label == "Capture task · Ctrl + Alt + K" &&
            !state.CanInvoke(items.Single(item => item.Command == TrayCommand.Preview)),
            "tray model preserves command order and shortcut label, with unavailable previews disabled");

        var active = state with { DismissLabel = "Cancel preview", SnoozeAvailable = true, SkipAvailable = true };
        Check(active.Items().Where(item => item.Selectable).Select(item => item.Command).Contains(TrayCommand.Snooze) &&
            !state.CanInvoke(active.Items().Single(item => item.Command == TrayCommand.Snooze)) &&
            !state.CanInvoke(active.Items().Single(item => item.Command == TrayCommand.Skip)),
            "tray model revalidates transient Snooze and Skip commands against current availability");
        Check(!(active with { DismissLabel = "End break" }).CanInvoke(
            active.Items().Single(item => item.Command == TrayCommand.EndBreak)) &&
            !(state with { ManuallyPaused = true }).CanInvoke(items.Single(item => item.Command == TrayCommand.TogglePause)),
            "a stale Cancel preview or Pause selection cannot invoke a changed command");

        var lifecycle = new TrayMenuLifetime();
        Action<TrayCommand> dispatch = _ => { };
        var first = new TrayOpening(700, dispatch);
        var next = new TrayOpening(700, dispatch);
        lifecycle.Open(first);
        Check(lifecycle.Current?.Source == 700 && lifecycle.TryClose(first) && lifecycle.Current is null &&
            !lifecycle.TryClose(first), "a tray dismissal clears the original source and dispatch opportunity once");
        lifecycle.Open(next);
        Check(!lifecycle.TryClose(first) && ReferenceEquals(lifecycle.Current, next) && lifecycle.TryClose(next),
            "late callbacks cannot close even a new tray opening with the same source");
    }

    private static void CheckCaptureMotionStudy(string path)
    {
        var store = new TaskStore(path);
        _ = new AfterbreakSession(new DispatcherQueue(), () => { }, store);
        Desktop.Current.Send(TrayCommand.Capture);
        Check(CaptureWindow.Current.IsVisible && CaptureWindow.Current.EntranceStarts == 0,
            "normal capture remains immediately available without a study entrance");
        Desktop.Current.Send(TrayCommand.Quit);

        _ = new AfterbreakSession(new DispatcherQueue(), () => { }, store, captureMotionStudy: true);
        var tray = Desktop.Current;
        var capture = CaptureWindow.Current;
        capture.CanPlace = false;
        tray.Send(TrayCommand.Capture);
        Check(!capture.IsVisible && capture.EntranceStarts == 0, "failed placement never requests a hidden capture entrance");

        capture.CanPlace = true;
        capture.FocusReady = false;
        tray.Send(TrayCommand.Capture);
        Check(capture.IsVisible && capture.EntranceStarts == 0, "an unready editing field skips the study entrance");
        capture.Cancel();

        capture.FocusReady = true;
        tray.Send(TrayCommand.Capture);
        Check(capture.IsVisible && capture.EntranceStarts == 1 && capture.EntranceRunning,
            "opt-in requests one entrance after fresh capture is shown");
        capture.Input = "Keep the title";
        capture.NextStep = "Keep the next step";
        capture.CanPlace = false;
        tray.Send(TrayCommand.Capture);
        capture.CanPlace = true;
        tray.Send(TrayCommand.Capture);
        Check(!capture.EntranceRunning && capture.EntranceStarts == 1 && capture.Input == "Keep the title" &&
            capture.NextStep == "Keep the next step", "failed and successful capture reentry keep both fields without a second entrance");
        capture.Cancel();

        tray.Send(TrayCommand.Capture);
        capture.Cancel();
        Check(!capture.IsVisible && !capture.EntranceRunning && capture.EntranceStarts == 2 &&
            capture.EntranceStoppedWhileVisible, "cancel stops study motion before hiding capture");

        tray.Send(TrayCommand.Capture);
        Check(capture.Save("Save during entrance", "A preserved note") && !capture.EntranceRunning &&
            capture.EntranceStarts == 3 && store.List(archived: false).Single().NextStep == "A preserved note",
            "capture can save a complete task while the study entrance is running");

        tray.Send(TrayCommand.Capture);
        tray.Send(TrayCommand.Quit);
        Check(capture.Closed && !capture.EntranceRunning && capture.EntranceStarts == 4,
            "quit stops an active capture entrance");
    }

    private static void CheckSession(string path)
    {
        var store = new TaskStore(path);
        var settings = ReminderSettings.Default with { BreakDisplay = BreakDisplay.OneTask };
        store.SaveReminders(settings, new ReminderScheduler(TimeProvider.System, settings).Checkpoint());
        store.Add("Original return task");
        var first = store.List(archived: false).Single();
        store.Move(first.Id, TaskStatus.InProgress);
        store.Edit(first.Id, first.Title, "Resume at page two");

        var queue = new DispatcherQueue();
        var didQuit = false;
        var session = new AfterbreakSession(queue, () => didQuit = true, store);
        var tray = Desktop.Current;
        var capture = CaptureWindow.Current;
        var startedAt = typeof(AfterbreakSession).GetField("startedAt", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Check the session clock before updating this test.");

        void Elapse()
        {
            startedAt.SetValue(session, Stopwatch.GetTimestamp() - 11 * Stopwatch.Frequency);
            queue.Timer.Fire();
        }

        void Preview(TrayCommand command = TrayCommand.Preview)
        {
            tray.Send(command);
            Elapse();
        }

        Check(!queue.Timer.Running && WarningWindow.Shows == 0, "startup stays quiet");
        tray.Send(TrayCommand.Tasks);
        var board = BoardWindow.Current;
        board.AddTask!();
        Check(capture.IsVisible && !tray.PreviewAvailable, "board Add uses capture and disables previews");
        capture.NextStep = "Note without a title";
        var returnsBeforeNoteOnly = SurfaceWindow.Returns;
        Check(!capture.Save("  ", capture.NextStep) && capture.IsVisible && store.List(archived: false).Count == 1 &&
            SurfaceWindow.Returns == returnsBeforeNoteOnly && capture.NextStep == "Note without a title",
            "a next step without a title keeps the draft and creates no task");
        capture.Cancel();

        var edit = new BoardWindow.CardFocus(first.Id, BoardWindow.CardAction.Edit);
        board.FocusedCard = edit;
        tray.Send(TrayCommand.Capture, board.Handle);
        Check(capture.Input == "" && capture.NextStep == "", "fresh capture clears both canceled fields");
        capture.Input = "Draft retained across storage failure";
        capture.NextStep = "  Check the original subtotal  ";
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using (var blocker = new SqliteConnection(connectionString))
        {
            blocker.Open();
            using var lockDatabase = blocker.BeginTransaction();
            Check(!capture.Save(capture.Input, capture.NextStep), "locked database reports failed capture save");
            var captures = capture.Shows;
            var warnings = WarningWindow.Shows;
            tray.Send(TrayCommand.Preview);
            tray.Send(TrayCommand.PreviewSampleBoard);
            board.FocusedCard = null;
            tray.Send(TrayCommand.Capture, board.Handle);
            Check(capture.IsVisible && capture.Input == "Draft retained across storage failure" && capture.Shows == captures
                && capture.NextStep == "  Check the original subtotal  " && WarningWindow.Shows == warnings && !queue.Timer.Running,
                "both failed draft fields survive preview commands and capture reinvocation");
            lockDatabase.Rollback();
        }

        Check(capture.Save(capture.Input, capture.NextStep), "same draft with its next step saves on retry");
        Check(!capture.IsVisible && tray.PreviewAvailable && SurfaceWindow.Returned == board.Handle && board.Refreshes == 1,
            "successful save returns to board and refreshes it");
        Check(board.LastRefreshFocus == edit, "retry after a failed save retains the original Edit target");
        var saved = new TaskStore(path).List(archived: false);
        Check(saved.Count == 2 && saved.Single(task => task.Status == TaskStatus.ToDo) is
            { Title: "Draft retained across storage failure", NextStep: "Check the original subtotal" },
            "retry commits one task and note that survive a new store instance");
        Check(!capture.Save(capture.Input, capture.NextStep) && store.List(archived: false).SequenceEqual(saved),
            "a repeated save after capture closes cannot duplicate the task");

        Preview();
        Check(BreakWindow.Layout == BreakLayout.Compact && BreakWindow.Text == first.Title && BreakWindow.Note == "Resume at page two",
            "compact preview selects the saved In progress title and note");
        var shownBreaks = BreakWindow.Shows;
        tray.Send(TrayCommand.PreviewSampleBoard);
        Check(BreakWindow.Shows == shownBreaks && BreakWindow.Layout == BreakLayout.Compact, "active preview ignores layout reentry");
        var returned = SurfaceWindow.Returns;
        Elapse();
        queue.Timer.Fire();
        Check(!queue.Timer.Running && tray.PreviewAvailable && SurfaceWindow.Returns == returned + 1, "natural expiry dismisses once");

        var beforeSample = store.List(archived: false);
        Preview(TrayCommand.PreviewSampleBoard);
        Check(BreakWindow.Layout == BreakLayout.SampleBoard && BreakWindow.Text is null && BreakWindow.Note is null &&
            store.List(archived: false).SequenceEqual(beforeSample), "sample board receives no saved content and changes no records");
        tray.Send(TrayCommand.EndBreak);

        store.SetArchived(first.Id, archived: true);
        Preview();
        Check(BreakWindow.Text == capture.Input && BreakWindow.Note == "Check the original subtotal",
            "archived In progress task falls back to the captured To do title and next step");
        tray.Send(TrayCommand.EndBreak);
        foreach (var task in store.List(archived: false))
        {
            store.Move(task.Id, TaskStatus.Done);
        }
        Preview();
        Check(BreakWindow.Layout == BreakLayout.TimerOnly && BreakWindow.Text is null,
            "Done-only tasks show the timer in One task mode");
        tray.Send(TrayCommand.EndBreak);

        tray.Send(TrayCommand.Preview);
        shownBreaks = BreakWindow.Shows;
        tray.Send(TrayCommand.EndBreak);
        Elapse();
        Check(BreakWindow.Shows == shownBreaks && !queue.Timer.Running, "cancelled warning never opens break");

        CheckCaptureFocus(tray, board, capture, store.List(archived: false).Single().Id);

        var trayEdit = new BoardWindow.CardFocus(store.List(archived: false)[0].Id, BoardWindow.CardAction.Edit);
        board.FocusedCard = trayEdit;
        var traySelect = tray.OpenTray(board.Handle);
        board.FocusedCard = null;
        traySelect(TrayCommand.Capture);
        Check(capture.Save("Capture from tray flyout") && !tray.MenuOpen && SurfaceWindow.Returned == board.Handle &&
            board.LastRefreshFocus == trayEdit, "tray capture uses the card focus saved before flyout activation");

        Preview();
        BreakWindow.Current.Foreground = false;
        tray.MenuOpen = true;
        tray.TrayMenuHasFocus = true;
        queue.Timer.Fire();
        Check(!tray.LastPresentationCheck && BreakWindow.Current.IsVisible,
            "opening our tray over a preview preserves its presentation exemption");
        tray.ChangeEligibility();
        Check(tray.LastPresentationCheck, "explicit Windows eligibility changes still recheck presentation over a tray");

        tray.Send(TrayCommand.Quit);
        Check(didQuit && tray.Disposed && capture.Closed && board.Closed, "quit closes board and capture and disposes desktop");
    }

    private static void CheckCaptureFocus(Desktop tray, BoardWindow board, CaptureWindow capture, long taskId)
    {
        var move = new BoardWindow.CardFocus(taskId, BoardWindow.CardAction.Move);
        board.FocusedCard = move;
        tray.Send(TrayCommand.Capture, board.Handle);
        Check(capture.Save("Capture while moving is focused") && board.LastRefreshFocus == move,
            "capture keeps Move distinct from Edit for the same task");

        var refreshes = board.Refreshes;
        tray.Send(TrayCommand.Capture, board.Handle);
        capture.Cancel();
        Check(board.Refreshes == refreshes && SurfaceWindow.Returned == board.Handle, "cancel returns without rebuilding the board");
        board.FocusedCard = null;
        board.AddTask!();
        Check(capture.Save("Capture from the board header") && board.LastRefreshFocus is null,
            "capture from the board header after cancel requests no card focus");

        const int external = 400;
        board.FocusedCard = move;
        tray.Send(TrayCommand.Capture, external);
        Check(capture.Save("Capture from another app") && SurfaceWindow.Returned == external && board.LastRefreshFocus is null,
            "capture from another app refreshes the board without requesting board focus");

        tray.Send(TrayCommand.Capture, board.Handle);
        tray.Send(TrayCommand.Capture, external);
        Check(capture.Save("Capture switched to another app") && SurfaceWindow.Returned == external && board.LastRefreshFocus is null,
            "changing the capture return window clears the old card focus");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed)
        {
            throw new InvalidOperationException(name);
        }
        Console.WriteLine($"PASS {name}");
    }

    private static void CheckReminderInterlock(string path)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var queue = new DispatcherQueue();
        var store = new TaskStore(path);
        store.Add("Private return task");
        _ = new AfterbreakSession(queue, () => { }, store, clock);
        var tray = Desktop.Current;
        var capture = CaptureWindow.Current;
        var schedulerTimer = queue.Timers[1];
        var enabled = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5, BreakDisplay = BreakDisplay.TimerOnly };

        void TickSeconds(int seconds)
        {
            for (var second = 0; second < seconds; second++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                schedulerTimer.Fire();
            }
        }

        var warnings = WarningWindow.Shows;
        tray.Send(TrayCommand.Preview);
        tray.Send(TrayCommand.Settings);
        Check(WarningWindow.Shows == warnings + 1 && !queue.Timer.Running && SettingsWindow.Current.IsVisible,
            "opening settings cancels a manual preview before an overlay can appear");
        var settings = SettingsWindow.Current;
        using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            blocker.Open();
            using var locked = blocker.BeginTransaction();
            Check(!settings.Save(enabled) && !schedulerTimer.Running, "failed opt-in keeps reminders off");
            locked.Rollback();
        }
        Check(settings.Save(enabled) && schedulerTimer.Running, "successful opt-in starts the scheduler");
        warnings = WarningWindow.Shows;
        tray.Send(TrayCommand.Capture, settings.Handle);
        capture.Input = "Settings capture draft";
        TickSeconds(8);
        Check(WarningWindow.Shows == warnings && capture.Input == "Settings capture draft" && settings.IsVisible,
            "capture and settings defer production warnings without losing drafts");
        Check(capture.Save(capture.Input) && SurfaceWindow.Returned == settings.Handle,
            "capture returns to a still-open settings window");

        tray.Send(TrayCommand.Tasks);
        var board = BoardWindow.Current;
        board.IsEditing = true;
        settings.Hide();
        TickSeconds(10);
        Check(WarningWindow.Shows == warnings && board.IsEditing, "the board editor keeps production reminders deferred");
        board.IsEditing = false;
        TickSeconds(4);
        tray.Send(TrayCommand.PreviewSampleBoard);
        TickSeconds(40);
        Check(queue.Timer.Running && WarningWindow.Shows == warnings + 1 && WarningWindow.Kind is null &&
            store.LoadReminders().Checkpoint?.CompletedShorts == 0, "the sample preview holds the scheduler without advancing cadence");
        tray.Send(TrayCommand.EndBreak);
        TickSeconds(3);
        Check(WarningWindow.Shows == warnings + 2 && WarningWindow.Kind == "Short break" && WarningWindow.Seconds is >= 8 and <= 10,
            "production resumes with a full scheduled warning after the preview");

        TickSeconds(WarningWindow.Seconds - 1);
        var breaks = BreakWindow.Shows;
        var reads = 0;
        tray.CheckSystemPause = () => ++reads == 1 ? ReminderPause.None : ReminderPause.Presentation;
        TickSeconds(1);
        Check(BreakWindow.Shows == breaks, "the second eligibility check can block a due overlay");
        tray.CheckSystemPause = null;
        tray.ChangeEligibility();
        Check(WarningWindow.Seconds == 10, "eligibility recovery starts a new warning");
        TickSeconds(10);
        Check(BreakWindow.Shows == breaks + 1 && BreakWindow.Layout == BreakLayout.TimerOnly && BreakWindow.Text is null,
            "scheduled timer-only break omits the saved task");
        tray.Send(TrayCommand.EndBreak);
        Check(store.LoadReminders().Checkpoint?.CompletedShorts == 0, "ending a scheduled break early advances no cadence");

        tray.Send(TrayCommand.TogglePause);
        tray.Send(TrayCommand.Preview);
        Check(!tray.PreviewAvailable && !queue.Timer.Running && store.LoadReminders().Checkpoint?.ManuallyPaused == true,
            "presentation pause persists and blocks explicit previews");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckFailedStartupRead(string path)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var original = ReminderSettings.Default with { Enabled = true, WorkSeconds = 64, BreakDisplay = BreakDisplay.TimerOnly };
        var store = new TaskStore(path);
        store.SaveReminders(original, new ReminderScheduler(clock, original).Checkpoint());
        var savedAppearance = new AppearancePreferences(AppearanceTheme.Dark, BreakScene.Daylight);
        store.SaveAppearance(savedAppearance);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using (var blocker = new SqliteConnection(connectionString))
        {
            blocker.Open();
            using var command = blocker.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE;";
            command.ExecuteNonQuery();
            _ = new AfterbreakSession(new DispatcherQueue(), () => { }, store, clock);
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }

        var tray = Desktop.Current;
        tray.Send(TrayCommand.TogglePause);
        Check(tray.ManuallyPaused && store.LoadReminders().Settings == original,
            "a transient startup read failure never overwrites saved settings with fallback defaults");
        tray.Send(TrayCommand.Settings);
        Check(SettingsWindow.Current.LoadedSettings == original, "opening settings retries the failed load");
        Check(WindowAppearance.Current.Preferences == AppearancePreferences.Default && store.LoadAppearance() == savedAppearance,
            "startup appearance read failure uses temporary defaults without persisting them");
        tray.Send(TrayCommand.Appearance);
        Check(AppearanceSettingsWindow.Current.Preferences == savedAppearance && WindowAppearance.Current.Preferences == savedAppearance,
            "opening Appearance retries the read and restores the complete saved preference pair");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckCaptureSettings(string path)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskStore(path);
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5, ShortSeconds = 10 };
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var queue = new DispatcherQueue();
        _ = new AfterbreakSession(queue, () => { }, store, clock);
        var tray = Desktop.Current;
        var timer = queue.Timers[1];
        var initialWarnings = WarningWindow.Shows;

        void Tick(int seconds)
        {
            for (var second = 0; second < seconds; second++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                timer.Fire();
            }
        }

        Tick(2);
        tray.Send(TrayCommand.CaptureSettings);
        var captureSettings = CaptureSettingsWindow.Current;
        timer.Fire();
        var checkpoint = store.LoadReminders();
        Check(captureSettings.Preferences is { GuideDismissed: false } &&
            CaptureShortcut.TryParse("Ctrl+Shift+7", out var chord) && captureSettings.Apply(chord) is null &&
            tray.CaptureBinding.Active == chord && captureSettings.DismissGuide(), "capture settings apply a shortcut and persist guidance dismissal");
        Check(store.LoadReminders() == checkpoint, "shortcut and guidance writes leave the reminder checkpoint unchanged");
        tray.Send(TrayCommand.Capture, captureSettings.Handle);
        Check(CaptureWindow.Current.Save("Capture with settings open") && SurfaceWindow.Returned == captureSettings.Handle && captureSettings.IsVisible,
            "capture returns to capture settings without replacing its draft");
        Tick(20);
        Check(WarningWindow.Shows == initialWarnings, "capture settings defer scheduled display without completing work");
        captureSettings.Hide();
        timer.Fire();
        Tick(3);
        Check(WarningWindow.Current.IsVisible, "closing capture settings resumes the original work interval");
        Tick(10);
        Tick(3);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 7, "a partial scheduled break is underway");

        tray.Send(TrayCommand.CaptureSettings);
        Tick(20);
        Check(!BreakWindow.Current.IsVisible && captureSettings.IsVisible &&
            captureSettings.Preferences is { GuideDismissed: true }, "opening capture settings defers the partial break and retains acknowledged guidance");
        captureSettings.Hide();
        timer.Fire();
        Check(WarningWindow.Current.IsVisible && WarningWindow.Seconds == 10, "resuming the partial break gives a full warning");
        Tick(10);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 7, "capture settings preserve the original break time remaining");
        Tick(7);
        Check(store.LoadReminders().Checkpoint?.CompletedShorts == 1, "the resumed break counts only after completing");

        tray.Send(TrayCommand.Quit);
        Check(captureSettings.Closed && tray.CaptureBinding.Active is null, "quit closes capture settings and releases the active shortcut");
        _ = new AfterbreakSession(new DispatcherQueue(), () => { }, new TaskStore(path), clock);
        Check(Desktop.Current.CaptureBinding.Active?.ToString() == "Ctrl+Shift+7", "startup re-registers the persisted shortcut");
        Desktop.Current.Send(TrayCommand.Quit);
    }

    private static void CheckAppearanceSettings(string path)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskStore(path);
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5, ShortSeconds = 10 };
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var queue = new DispatcherQueue();
        _ = new AfterbreakSession(queue, () => { }, store, clock);
        var tray = Desktop.Current;
        var appearance = WindowAppearance.Current;
        var timer = queue.Timers[1];

        void Tick(int seconds)
        {
            for (var second = 0; second < seconds; second++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                timer.Fire();
            }
        }

        Tick(18);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 7, "scheduled break is partly over before Appearance opens");
        tray.Send(TrayCommand.Appearance);
        var window = AppearanceSettingsWindow.Current;
        Tick(20);
        Check(window.IsVisible && !BreakWindow.Current.IsVisible && appearance.Preferences == AppearancePreferences.Default,
            "Appearance defers scheduled display without replacing applied settings");
        var selected = new AppearancePreferences(AppearanceTheme.Light, BreakScene.Daylight);
        using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            blocker.Open();
            using var locked = blocker.BeginTransaction();
            Check(!window.Save(selected) && appearance.Preferences == AppearancePreferences.Default && window.IsVisible,
                "a failed appearance write leaves the current appearance applied and the form available");
            locked.Rollback();
        }
        var beforeSave = store.LoadReminders();
        Check(window.Save(selected) && appearance.Preferences == selected && new TaskStore(path).LoadAppearance() == selected &&
            store.LoadReminders() == beforeSave, "successful appearance save applies the preference and leaves the reminder checkpoint intact");
        tray.Send(TrayCommand.Capture, window.Handle);
        CaptureWindow.Current.Input = "Keep the draft";
        tray.Send(TrayCommand.Appearance);
        Check(CaptureWindow.Current.Input == "Keep the draft" && CaptureWindow.Current.IsVisible,
            "opening Appearance while capturing leaves the capture draft intact");
        CaptureWindow.Current.Cancel();
        Check(SurfaceWindow.Returned == window.Handle, "capture returns to Appearance");
        window.Hide();
        timer.Fire();
        Check(WarningWindow.Current.IsVisible && WarningWindow.Seconds == 10, "Appearance resumes a partial break with a fresh warning");
        Tick(10);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 7, "appearance changes keep the remaining break time");
        Tick(7);
        Check(store.LoadReminders().Checkpoint?.CompletedShorts == 1, "the resumed break advances cadence only on completion");
        tray.Send(TrayCommand.Quit);
        Check(appearance.Disposed && window.Closed, "Quit disposes appearance tracking and closes its window");
    }
}
