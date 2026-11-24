using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;

namespace OpenBreak;

internal static class Program
{
    private static void Main()
    {
        var temporary = Directory.CreateTempSubdirectory("OpenBreak-session-");
        try
        {
            CheckSession(Path.Combine(temporary.FullName, "tasks.db"));
            CheckIgnoredCaptureInsert(Path.Combine(temporary.FullName, "ignored-capture.db"));
            BreakDisplayChecks.Run(temporary.FullName);
            WarningInputSessionChecks.Run(Path.Combine(temporary.FullName, "warning-input.db"));
            CheckTrayMenuPolicy();
            CheckCaptureMotionStudy(Path.Combine(temporary.FullName, "motion-study.db"));
            CheckReminderInterlock(Path.Combine(temporary.FullName, "reminders.db"));
            CheckFailedStartupRead(Path.Combine(temporary.FullName, "startup.db"));
            CheckCaptureSettings(Path.Combine(temporary.FullName, "capture-settings.db"));
            CheckAppearanceSettings(Path.Combine(temporary.FullName, "appearance.db"));
            CheckMainNavigation(Path.Combine(temporary.FullName, "unified-shell.db"));
            CheckLaunchReveal(Path.Combine(temporary.FullName, "launch-reveal.db"));
            CheckFocusHome(Path.Combine(temporary.FullName, "focus-home.db"));
            CountdownChecks.Run(Path.Combine(temporary.FullName, "countdown.db"));
            CheckRhythmSession(Path.Combine(temporary.FullName, "rhythm.db"));
            CheckQuietReminderActionFailure(Path.Combine(temporary.FullName, "quiet-reminder-error.db"));
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

        _ = new OpenBreakSession(new DispatcherQueue(), () => { }, store);
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
            [TrayCommand.OpenMain, TrayCommand.Capture,
                TrayCommand.Preview, TrayCommand.PreviewSampleBoard, TrayCommand.Settings, TrayCommand.TogglePause, TrayCommand.Quit]) &&
            items.Single(item => item.Command == TrayCommand.Capture).Label == "Capture task · Ctrl + Alt + K" &&
            !state.CanInvoke(items.Single(item => item.Command == TrayCommand.Preview)),
            "tray model preserves command order and shortcut label, with unavailable previews disabled");

        var active = state with { DismissLabel = "Cancel preview", SnoozeAvailable = true, SkipAvailable = true };
        Check(active.Items().Where(item => item.Selectable).Select(item => item.Command).Contains(TrayCommand.Snooze) &&
            !state.CanInvoke(active.Items().Single(item => item.Command == TrayCommand.Snooze)) &&
            !state.CanInvoke(active.Items().Single(item => item.Command == TrayCommand.Skip)),
            "tray model revalidates transient Snooze and Skip commands against current availability");
        var customSnooze = active with { SnoozeSeconds = 73 };
        Check(customSnooze.Items().Single(item => item.Command == TrayCommand.Snooze).Label == "Snooze 1 min 13 sec" &&
            !customSnooze.CanInvoke(active.Items().Single(item => item.Command == TrayCommand.Snooze)),
            "a changed custom snooze updates its tray label and invalidates the previous selection");
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
        _ = new OpenBreakSession(new DispatcherQueue(), () => { }, store);
        Desktop.Current.Send(TrayCommand.Capture);
        Check(CaptureWindow.Current.IsVisible && CaptureWindow.Current.EntranceStarts == 0,
            "normal capture remains immediately available without a study entrance");
        Desktop.Current.Send(TrayCommand.Quit);

        _ = new OpenBreakSession(new DispatcherQueue(), () => { }, store, captureMotionStudy: true);
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
        var session = new OpenBreakSession(queue, () => didQuit = true, store);
        var tray = Desktop.Current;
        var capture = CaptureWindow.Current;
        var startedAt = typeof(OpenBreakSession).GetField("startedAt", BindingFlags.Instance | BindingFlags.NonPublic)
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
        var board = TasksPage.Current;
        tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
        Check(capture.IsVisible && !tray.PreviewAvailable, "global capture opens over Tasks and disables previews");
        capture.NextStep = "Note without a title";
        var returnsBeforeNoteOnly = SurfaceWindow.Returns;
        Check(!capture.Save("  ", capture.NextStep) && capture.IsVisible && store.List(archived: false).Count == 1 &&
            SurfaceWindow.Returns == returnsBeforeNoteOnly && capture.NextStep == "Note without a title",
            "a next step without a title keeps the draft and creates no task");
        capture.Cancel();

        var actions = new TasksPage.CardFocus(first.Id, TasksPage.CardAction.Actions);
        board.FocusedCard = actions;
        tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
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
            tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
            Check(capture.IsVisible && capture.Input == "Draft retained across storage failure" && capture.Shows == captures
                && capture.NextStep == "  Check the original subtotal  " && WarningWindow.Shows == warnings && !queue.Timer.Running,
                "both failed draft fields survive preview commands and capture reinvocation");
            lockDatabase.Rollback();
        }

        Check(capture.Save(capture.Input, capture.NextStep), "same draft with its next step saves on retry");
        Check(!capture.IsVisible && tray.PreviewAvailable && SurfaceWindow.Returned == MainWindow.Current.Handle && board.Refreshes == 1,
            "successful save returns to board and refreshes it");
        Check(board.LastRefreshFocus == actions, "retry after a failed save retains the original card Actions target");
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

        var trayActions = new TasksPage.CardFocus(store.List(archived: false)[0].Id, TasksPage.CardAction.Actions);
        board.FocusedCard = trayActions;
        var traySelect = tray.OpenTray(MainWindow.Current.Handle);
        board.FocusedCard = null;
        traySelect(TrayCommand.Capture);
        Check(capture.Save("Capture from tray flyout") && !tray.MenuOpen && SurfaceWindow.Returned == MainWindow.Current.Handle &&
            board.LastRefreshFocus == trayActions, "tray capture uses the card focus saved before flyout activation");

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
        Check(didQuit && tray.Disposed && capture.Closed && MainWindow.Current.Closed, "quit closes board and capture and disposes desktop");
    }

    private static void CheckCaptureFocus(Desktop tray, TasksPage board, CaptureWindow capture, long taskId)
    {
        var actions = new TasksPage.CardFocus(taskId, TasksPage.CardAction.Actions);
        board.FocusedCard = actions;
        tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
        Check(capture.Save("Capture while task Actions is focused") && board.LastRefreshFocus == actions,
            "capture refresh requests the Actions control for the original task ID");

        var refreshes = board.Refreshes;
        tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
        capture.Cancel();
        Check(board.Refreshes == refreshes && SurfaceWindow.Returned == MainWindow.Current.Handle, "cancel returns without rebuilding the board");
        board.FocusedCard = null;
        tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
        Check(capture.Save("Capture from the board header") && board.LastRefreshFocus is null,
            "capture from the board header after cancel requests no card focus");

        const int external = 400;
        board.FocusedCard = actions;
        tray.Send(TrayCommand.Capture, external);
        Check(capture.Save("Capture from another app") && SurfaceWindow.Returned == external && board.LastRefreshFocus is null,
            "capture from another app refreshes the board without requesting board focus");

        tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
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
        _ = new OpenBreakSession(queue, () => { }, store, clock);
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
        Check(WarningWindow.Shows == warnings + 1 && !queue.Timer.Running && MainWindow.Current.IsVisible,
            "opening settings cancels a manual preview before an overlay can appear");
        var settings = ReminderSettingsPage.Current;
        using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            blocker.Open();
            using var locked = blocker.BeginTransaction();
            Check(!settings.Save(enabled) && !schedulerTimer.Running, "failed opt-in keeps reminders off");
            locked.Rollback();
        }
        Check(settings.Save(enabled) && schedulerTimer.Running, "successful opt-in starts the scheduler");
        warnings = WarningWindow.Shows;
        tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
        capture.Input = "Settings capture draft";
        TickSeconds(8);
        Check(WarningWindow.Shows == warnings && capture.Input == "Settings capture draft" && MainWindow.Current.IsVisible,
            "capture and settings defer production warnings without losing drafts");
        Check(capture.Save(capture.Input) && SurfaceWindow.Returned == MainWindow.Current.Handle,
            "capture returns to a still-open settings window");

        tray.Send(TrayCommand.Tasks);
        var board = TasksPage.Current;
        board.IsEditing = true;
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
            _ = new OpenBreakSession(new DispatcherQueue(), () => { }, store, clock);
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }

        var tray = Desktop.Current;
        tray.Send(TrayCommand.TogglePause);
        Check(tray.ManuallyPaused && store.LoadReminders().Settings == original,
            "a transient startup read failure never overwrites saved settings with fallback defaults");
        tray.Send(TrayCommand.Settings);
        Check(ReminderSettingsPage.Current.LoadedSettings == original, "opening settings retries the failed load");
        Check(WindowAppearance.Current.Preferences == AppearancePreferences.Default && store.LoadAppearance() == savedAppearance,
            "startup appearance read failure uses temporary defaults without persisting them");
        tray.Send(TrayCommand.Appearance);
        Check(AppearanceSettingsPage.Current.Preferences == savedAppearance && WindowAppearance.Current.Preferences == savedAppearance,
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
        _ = new OpenBreakSession(queue, () => { }, store, clock);
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
        var captureSettings = CaptureSettingsPage.Current;
        timer.Fire();
        var checkpoint = store.LoadReminders();
        Check(captureSettings.Preferences is { GuideDismissed: false } &&
            CaptureShortcut.TryParse("Ctrl+Shift+7", out var chord) && captureSettings.Apply(chord) is null &&
            tray.CaptureBinding.Active == chord && captureSettings.DismissGuide(), "capture settings apply a shortcut and persist guidance dismissal");
        Check(store.LoadReminders() == checkpoint, "shortcut and guidance writes leave the reminder checkpoint unchanged");
        MainWindow.Current.RequestQuickCapture();
        Check(CaptureWindow.Current.Save("Capture with settings open") && SurfaceWindow.Returned == MainWindow.Current.Handle && MainWindow.Current.IsVisible,
            "shell quick capture returns to capture settings without replacing its draft");
        captureSettings.IsDirty = true;
        timer.Fire();
        Tick(20);
        Check(WarningWindow.Shows == initialWarnings, "a dirty capture page defers scheduled display without completing work");
        MainWindow.Current.Hide();
        timer.Fire();
        Tick(3);
        Check(WarningWindow.Current.IsVisible, "closing capture settings resumes the original work interval");
        Tick(10);
        Tick(3);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 7, "a partial scheduled break is underway");

        tray.Send(TrayCommand.CaptureSettings);
        Tick(20);
        Check(!BreakWindow.Current.IsVisible && MainWindow.Current.IsVisible &&
            captureSettings.Preferences is { GuideDismissed: true }, "opening capture settings defers the partial break and retains acknowledged guidance");
        MainWindow.Current.Hide();
        timer.Fire();
        Check(WarningWindow.Current.IsVisible && WarningWindow.Seconds == 10, "resuming the partial break gives a full warning");
        Tick(10);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 7, "capture settings preserve the original break time remaining");
        Tick(7);
        Check(store.LoadReminders().Checkpoint?.CompletedShorts == 1, "the resumed break counts only after completing");

        tray.Send(TrayCommand.Quit);
        Check(MainWindow.Current.Closed && tray.CaptureBinding.Active is null, "quit closes capture settings and releases the active shortcut");
        _ = new OpenBreakSession(new DispatcherQueue(), () => { }, new TaskStore(path), clock);
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
        _ = new OpenBreakSession(queue, () => { }, store, clock);
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
        var window = AppearanceSettingsPage.Current;
        window.IsDirty = true;
        Tick(20);
        Check(MainWindow.Current.IsVisible && !BreakWindow.Current.IsVisible && appearance.Preferences == AppearancePreferences.Default,
            "Appearance defers scheduled display without replacing applied settings");
        var selected = new AppearancePreferences(AppearanceTheme.Light, BreakScene.Daylight);
        using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            blocker.Open();
            using var locked = blocker.BeginTransaction();
            Check(!window.Save(selected) && appearance.Preferences == AppearancePreferences.Default && MainWindow.Current.IsVisible,
                "a failed appearance write leaves the current appearance applied and the form available");
            locked.Rollback();
        }
        var beforeSave = store.LoadReminders();
        Check(window.Save(selected) && appearance.Preferences == selected && new TaskStore(path).LoadAppearance() == selected &&
            store.LoadReminders() == beforeSave, "successful appearance save applies the preference and leaves the reminder checkpoint intact");
        tray.Send(TrayCommand.Capture, MainWindow.Current.Handle);
        CaptureWindow.Current.Input = "Keep the draft";
        tray.Send(TrayCommand.Appearance);
        Check(CaptureWindow.Current.Input == "Keep the draft" && CaptureWindow.Current.IsVisible,
            "opening Appearance while capturing leaves the capture draft intact");
        CaptureWindow.Current.Cancel();
        Check(SurfaceWindow.Returned == MainWindow.Current.Handle, "capture returns to Appearance");
        MainWindow.Current.Hide();
        timer.Fire();
        Check(WarningWindow.Current.IsVisible && WarningWindow.Seconds == 10, "Appearance resumes a partial break with a fresh warning");
        Tick(10);
        Check(BreakWindow.Current.IsVisible && BreakWindow.Current.Seconds == 7, "appearance changes keep the remaining break time");
        Tick(7);
        Check(store.LoadReminders().Checkpoint?.CompletedShorts == 1, "the resumed break advances cadence only on completion");
        tray.Send(TrayCommand.Quit);
        Check(appearance.Disposed && MainWindow.Current.Closed, "Quit disposes appearance tracking and closes its window");
    }

    private static void CheckMainNavigation(string path)
    {
        var store = new TaskStore(path);
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5 };
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var queue = new DispatcherQueue();
        _ = new OpenBreakSession(queue, () => { }, store, clock);
        var tray = Desktop.Current;
        var timer = queue.Timers[1];

        void Tick(int seconds)
        {
            for (var second = 0; second < seconds; second++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                timer.Fire();
            }
        }

        tray.Send(TrayCommand.OpenMain);
        var host = MainWindow.Current;
        Tick(2);
        tray.Send(TrayCommand.Settings);
        Check(ReferenceEquals(MainWindow.Current, host) && tray.MainHandle == host.Handle &&
            host.CurrentPage == ManagementPage.Breaks && !host.DefersReminders,
            "Open OpenBreak and the Settings deep link use one host and a clean page does not defer");
        var draft = settings with { WorkSeconds = 42 };
        host.Breaks.Draft = draft;
        Tick(12);
        Check(host.DefersReminders && !WarningWindow.Current.IsVisible && store.LoadReminders().Settings == settings,
            "an active dirty settings page defers reminders without saving its draft");
        host.Navigate(ManagementPage.Tasks);
        Tick(4);
        Check(WarningWindow.Current.IsVisible && host.Breaks.Draft == draft && !host.DefersReminders,
            "visiting Tasks resumes the scheduler and retains the hidden settings draft");
        tray.Send(TrayCommand.EndBreak);

        tray.Send(TrayCommand.Settings);
        host.Hide();
        Check(!host.DefersReminders, "closing the main window hides the dirty page and releases the scheduler");
        tray.Send(TrayCommand.OpenMain);
        Check(ReferenceEquals(MainWindow.Current, host) && host.CurrentPage == ManagementPage.Breaks &&
            host.Breaks.Draft == draft && host.Breaks.Save(draft) && host.IsVisible && !host.DefersReminders,
            "reopening and saving restores the same section and leaves the main window open");

        host.Navigate(ManagementPage.Tasks);
        tray.Send(TrayCommand.Capture, host.Handle);
        host.Navigate(ManagementPage.Appearance);
        host.Foreground = true;
        tray.Send(TrayCommand.Capture, host.Handle);
        Check(CaptureWindow.Current.Save("Capture after navigation") && host.RestoredFocus?.Page == ManagementPage.Appearance &&
            SurfaceWindow.Returned == host.Handle, "explicit same-window capture reentry takes the new page bookmark");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckLaunchReveal(string path)
    {
        var store = new TaskStore(path);
        var queue = new DispatcherQueue();
        var session = new OpenBreakSession(queue, () => { }, store);
        var tray = Desktop.Current;
        Check(tray.MainHandle == 0 && !queue.Timers[1].Running,
            "a quiet start leaves the main window unopened with reminders still off");
        session.RevealFromLaunch();
        var host = MainWindow.Current;
        Check(host.IsVisible && host.LastActivate && host.CurrentPage == ManagementPage.Focus && tray.MainHandle == host.Handle,
            "a normal launch reveals the Focus page in the main window");

        host.FocusHome.OpenGettingStarted();
        Check(host.CurrentPage == ManagementPage.Capture && host.Capture.GuideVisible && host.Capture.DismissGuide(),
            "Getting started opens in the main shell and supports durable dismissal");
        host.Navigate(ManagementPage.Focus);
        host.FocusHome.OpenGettingStarted();
        Check(host.CurrentPage == ManagementPage.Capture && host.Capture.GuideVisible && store.LoadCapturePreferences().GuideDismissed,
            "the Focus entry reopens an acknowledged guide while retaining its saved preference");
        host.Capture.NavigateFromGuide(ManagementPage.Tasks);
        Check(host.CurrentPage == ManagementPage.Tasks && !store.LoadReminders().Settings.Enabled,
            "the guide opens Tasks without enabling scheduled breaks");
        host.Navigate(ManagementPage.Focus);
        host.FocusHome.OpenGettingStarted();
        host.Capture.NavigateFromGuide(ManagementPage.Breaks);
        Check(host.CurrentPage == ManagementPage.Breaks && !store.LoadReminders().Settings.Enabled,
            "the guide opens Breaks and leaves scheduled breaks off");
        var draft = host.Breaks.Draft with { WorkSeconds = 175 };
        host.Breaks.Draft = draft;
        host.Hide();
        session.RevealFromLaunch();
        Check(ReferenceEquals(host, MainWindow.Current) && host.IsVisible && host.LastActivate &&
            host.CurrentPage == ManagementPage.Breaks && host.Breaks.Draft == draft,
            "a repeated launch reuses the hidden host, its page, and its draft");

        const int captureSource = 801;
        tray.Send(TrayCommand.Capture, captureSource);
        var capture = CaptureWindow.Current;
        capture.Input = "Unsaved capture on repeated launch";
        host.Hide();
        session.RevealFromLaunch();
        Check(host.IsVisible && !host.LastActivate && capture.IsVisible && capture.Input == "Unsaved capture on repeated launch" &&
            capture.Save(capture.Input) && SurfaceWindow.Returned == captureSource,
            "repeated launch leaves active capture focused and keeps its original return source");

        var reminders = store.LoadReminders();
        tray.Send(TrayCommand.Preview);
        var warningCount = WarningWindow.Shows;
        session.RevealFromLaunch();
        Check(host.LastActivate && WarningWindow.Current.IsVisible && WarningWindow.Shows == warningCount &&
            store.LoadReminders() == reminders, "launch reveal leaves a visible warning and reminder state intact");

        const int breakSource = 802;
        tray.WorkSource = breakSource;
        var startedAt = typeof(OpenBreakSession).GetField("startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        startedAt.SetValue(session, Stopwatch.GetTimestamp() - 11 * Stopwatch.Frequency);
        queue.Timer.Fire();
        var breakStart = startedAt.GetValue(session);
        host.Hide();
        session.RevealFromLaunch();
        Check(host.IsVisible && !host.LastActivate && BreakWindow.Current.IsVisible && queue.Timer.Running &&
            Equals(startedAt.GetValue(session), breakStart) && store.LoadReminders() == reminders,
            "launch reveal during a break makes no activation request and preserves the countdown");
        BreakWindow.Current.End();
        Check(SurfaceWindow.Returned == breakSource && !BreakWindow.Current.IsVisible,
            "ending the break after a reveal still returns to the original work window");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckFocusHome(string path)
    {
        var store = new TaskStore(path);
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var settings = ReminderSettings.Default with { WorkSeconds = 12, ShortSeconds = 3, LongSeconds = 8 };
        var initial = new ReminderScheduler(clock, settings).Checkpoint();
        store.SaveReminders(settings, initial);
        store.Add("First task", "Open the file");
        store.Add("In-progress task", "Check the last line");
        var active = store.List(archived: false)[1];
        store.Move(active.Id, TaskStatus.InProgress);
        var queue = new DispatcherQueue();
        var session = new OpenBreakSession(queue, () => { }, store, clock);
        session.RevealFromLaunch();
        var tray = Desktop.Current;
        var host = MainWindow.Current;
        var focus = host.FocusHome;
        Check(focus.Snapshot is { Enabled: false, Seconds: 12, NextTask.Title: "In-progress task" } &&
            store.LoadReminders() == (settings, initial) && !host.DefersReminders,
            "opening Focus shows configured idle duration and the actual return task without opting in");

        using var rejection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        rejection.Open();
        using var trigger = rejection.CreateCommand();
        void RejectWrites(bool reject)
        {
            trigger.CommandText = reject
                ? "CREATE TRIGGER reject_focus BEFORE UPDATE ON reminder_state BEGIN SELECT RAISE(ABORT, 'write failure'); END;"
                : "DROP TRIGGER reject_focus;";
            trigger.ExecuteNonQuery();
        }
        void Tick(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                queue.Timers[1].Fire();
            }
        }
        bool Can(FocusAction action) => (focus.Snapshot.Actions & action) == action;

        focus.Navigate(ManagementPage.Breaks);
        host.Breaks.Draft = settings with { WorkSeconds = 42 };
        host.Navigate(ManagementPage.Focus);
        RejectWrites(true);
        Check(focus.Execute(FocusAction.Start) is not null && !focus.Snapshot.Enabled && store.LoadReminders() == (settings, initial),
            "a failed Focus start leaves runtime and durable configuration unchanged");
        RejectWrites(false);
        Check(focus.Execute(FocusAction.Start) is null && focus.Snapshot is { Enabled: true, Seconds: 12 } &&
            host.Breaks.Draft == (settings with { Enabled = true, WorkSeconds = 42 }),
            "Focus start opts in using saved timing and rebases the enabled toggle without losing a settings draft");
        store.Edit(active.Id, "Updated task", "Keep going");
        Tick(4);
        Check(focus.Snapshot is { Phase: ReminderPhase.Work, Seconds: 8, NextTask.Title: "In-progress task" },
            "the Focus countdown uses the scheduler tick and keeps cached task data between visits");
        focus.Navigate(ManagementPage.Tasks);
        host.Navigate(ManagementPage.Focus);
        Check(focus.Snapshot.NextTask?.Title == "Updated task", "returning from Tasks refreshes the saved task card");

        var beforePause = store.LoadReminders();
        RejectWrites(true);
        Check(focus.Execute(FocusAction.Pause) is not null && Can(FocusAction.Pause) && store.LoadReminders() == beforePause,
            "a rejected work-pause write leaves the running interval and saved checkpoint untouched");
        RejectWrites(false);
        Check(focus.Execute(FocusAction.Pause) is null && Can(FocusAction.Resume | FocusAction.Preview) &&
            store.LoadReminders().Checkpoint is { WorkPaused: true, Remaining.TotalSeconds: 8 },
            "work pause immediately saves the remaining interval and keeps preview eligible");
        Check(focus.Execute(FocusAction.Preview) is null && focus.Snapshot is { PreviewPhase: ReminderPhase.Warning, Seconds: 8 } &&
            Can(FocusAction.CancelPreview) && WarningWindow.Current.IsVisible,
            "a manual preview reports its own warning while the scheduled work countdown remains frozen");
        var startedAt = typeof(OpenBreakSession).GetField("startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        startedAt.SetValue(session, Stopwatch.GetTimestamp() - 11 * Stopwatch.Frequency);
        queue.Timer.Fire();
        Check(focus.Snapshot is { PreviewPhase: ReminderPhase.Break, Seconds: 8, CompletedShorts: 0 } &&
            focus.Execute(FocusAction.CancelPreview) is null && focus.Snapshot.PreviewPhase is null,
            "the manual break has its own phase and cancel leaves scheduled cadence intact");

        tray.Send(TrayCommand.TogglePause);
        tray.SystemPause = ReminderPause.Locked;
        tray.ChangeEligibility();
        Tick(35);
        var overlap = ReminderPause.Work | ReminderPause.Manual | ReminderPause.Locked;
        Check(focus.Snapshot is { Seconds: 8 } && focus.Snapshot.PausedBy == overlap && !Can(FocusAction.Preview),
            "Focus reports all overlapping pauses while presentation and Windows block preview");
        tray.Send(TrayCommand.Quit);
        queue = new DispatcherQueue();
        session = new OpenBreakSession(queue, () => { }, store, clock);
        session.RevealFromLaunch();
        tray = Desktop.Current;
        focus = MainWindow.Current.FocusHome;
        tray.SystemPause = ReminderPause.Locked;
        tray.ChangeEligibility();
        Check(focus.Snapshot is { Enabled: true, Seconds: 8 } && focus.Snapshot.PausedBy == overlap &&
            focus.Execute(FocusAction.Resume) is null && focus.Snapshot.PausedBy == (overlap & ~ReminderPause.Work),
            "restart restores Work pause, and Focus resume leaves presentation and Windows reasons in force");
        tray.SystemPause = ReminderPause.None;
        tray.ChangeEligibility();
        tray.Send(TrayCommand.TogglePause);
        Tick(8);
        Check(focus.Snapshot.Phase == ReminderPhase.Warning && Can(FocusAction.Snooze | FocusAction.Skip) && !Can(FocusAction.Pause),
            "a due warning exposes snooze and skip instead of Work pause");
        Check(focus.Execute(FocusAction.Snooze) is null && focus.Snapshot is { Phase: ReminderPhase.Snoozed, Seconds: 300 } &&
            focus.Execute(FocusAction.Skip) is null && focus.Snapshot is { Phase: ReminderPhase.Work, Seconds: 12 },
            "Focus snooze and skip use the scheduled warning transitions");
        Tick(22);
        Check(focus.Snapshot is { Phase: ReminderPhase.Break, Seconds: 3 } && Can(FocusAction.EndBreak) &&
            focus.Execute(FocusAction.EndBreak) is null && focus.Snapshot is { Phase: ReminderPhase.Work, CompletedShorts: 0 },
            "ending a scheduled break from Focus retains the existing early-end cadence rule");

        focus.Execute(FocusAction.Pause);
        var beforeStop = store.LoadReminders();
        RejectWrites(true);
        Check(focus.Execute(FocusAction.Stop) is not null && Can(FocusAction.Resume) && store.LoadReminders() == beforeStop,
            "a rejected Focus stop retains both opt-in and work pause");
        RejectWrites(false);
        Check(focus.Execute(FocusAction.Stop) is null && !focus.Snapshot.Enabled &&
            store.LoadReminders() is { Settings.Enabled: false, Checkpoint.WorkPaused: false } &&
            focus.Execute(FocusAction.Start) is null && focus.Snapshot is { Seconds: 12, PausedBy: ReminderPause.None },
            "Focus stop disables reminders and clears work pause before the next fresh start");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckQuietReminderActionFailure(string path)
    {
        var store = new TaskStore(path);
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5, ShortSeconds = 10 };
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var queue = new DispatcherQueue();
        _ = new OpenBreakSession(queue, () => { }, store, clock);
        var tray = Desktop.Current;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var trigger = connection.CreateCommand();
        void RejectWrites(bool reject)
        {
            trigger.CommandText = reject
                ? "CREATE TRIGGER reject_action BEFORE UPDATE ON reminder_state BEGIN SELECT RAISE(IGNORE); END;"
                : "DROP TRIGGER reject_action;";
            trigger.ExecuteNonQuery();
        }
        void Tick(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                queue.Timers[1].Fire();
            }
        }

        Tick(5);
        var warning = WarningWindow.Current;
        var checkpoint = store.LoadReminders();
        RejectWrites(true);
        warning.Snooze();
        Check(tray.MainHandle == 0 && warning.IsVisible && warning.SaveError == tray.ReminderStatus && warning.SaveError is not null &&
            store.LoadReminders() == checkpoint, "quiet-start warning reports an ignored snooze without revealing the app or mutating the reminder");
        RejectWrites(false);
        warning.Snooze();
        Check(!warning.IsVisible && store.LoadReminders().Checkpoint?.Phase == ReminderPhase.Snoozed,
            "retry from the warning saves one snooze after storage recovers");

        Tick(310);
        var overlay = BreakWindow.Current;
        checkpoint = store.LoadReminders();
        RejectWrites(true);
        overlay.End();
        Check(tray.MainHandle == 0 && overlay.IsVisible && overlay.SaveError == tray.ReminderStatus && overlay.SaveError is not null &&
            store.LoadReminders() == checkpoint, "quiet-start break reports an ignored early end and retains the running break");
        RejectWrites(false);
        overlay.End();
        Check(!overlay.IsVisible && store.LoadReminders().Checkpoint is { Phase: ReminderPhase.Work, CompletedShorts: 0 },
            "retry from the break keeps the previous cadence and returns to work");
        tray.Send(TrayCommand.Quit);
    }

    private static void CheckRhythmSession(string path)
    {
        var store = new TaskStore(path);
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var settings = ReminderSettings.Default with
        {
            Enabled = true, WorkSeconds = 5, ShortSeconds = 3, LongSeconds = 7, ShortsBeforeLong = 1, SnoozeSeconds = 73,
        };
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var queue = new DispatcherQueue();
        var session = new OpenBreakSession(queue, () => { }, store, clock);
        session.RevealFromLaunch();
        var host = MainWindow.Current;
        var focus = host.FocusHome;
        var tray = Desktop.Current;
        var warning = WarningWindow.Current;

        void Tick(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                queue.Timers[1].Fire();
            }
        }

        Tick(5);
        Check(focus.Snapshot is { Phase: ReminderPhase.Warning, UpcomingBreakSeconds: 3, SnoozeSeconds: 73, ShortsBeforeLong: 1 } &&
            warning.IsVisible && WarningWindow.Kind == "Short break" && warning.SnoozeSeconds == 73 && tray.SnoozeSeconds == 73,
            "one configured snooze duration reaches Focus, the warning and the tray");
        Check(focus.Execute(FocusAction.Snooze) is null && focus.Snapshot is { Phase: ReminderPhase.Snoozed, Seconds: 73 } &&
            store.LoadReminders().Checkpoint is { Remaining.TotalSeconds: 73 }, "the Focus action persists the configured snooze");
        Tick(86);
        Check(focus.Snapshot is { Phase: ReminderPhase.Work, CompletedShorts: 1, NextBreakIsLong: true, UpcomingBreakSeconds: 7 },
            "Focus reflects the configured threshold and upcoming long break after a single short completion");
        Tick(5);
        Check(warning.IsVisible && WarningWindow.Kind == "Long break", "the scheduled warning agrees with the Focus break kind");
        tray.Send(TrayCommand.Snooze);
        Check(focus.Snapshot is { Phase: ReminderPhase.Snoozed, Seconds: 73, NextBreakIsLong: true } &&
            focus.Execute(FocusAction.Skip) is null && focus.Snapshot.NextBreakIsLong,
            "tray snooze and skipping a pending long break preserve the shared cadence");
        host.Navigate(ManagementPage.Breaks);
        var shortOnly = settings with { LongBreaks = false };
        Check(host.Breaks.Save(shortOnly) && store.LoadReminders() is { Settings.LongBreaks: false, Checkpoint.CompletedShorts: 0 },
            "saving short-only rhythm persists the setting and clears a previous due long break");
        host.Navigate(ManagementPage.Focus);
        Tick(6);
        Check(focus.Snapshot is { LongBreaks: false, NextBreakIsLong: false, UpcomingBreakSeconds: 3 } &&
            warning.IsVisible && WarningWindow.Kind == "Short break", "short-only status and warning agree after settings navigation");
        tray.Send(TrayCommand.Quit);
    }
}
