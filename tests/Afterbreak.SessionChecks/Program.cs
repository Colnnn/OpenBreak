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
            CheckReminderInterlock(Path.Combine(temporary.FullName, "reminders.db"));
            CheckFailedStartupRead(Path.Combine(temporary.FullName, "startup.db"));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static void CheckSession(string path)
    {
        var store = new TaskStore(path);
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
        capture.Cancel();

        var edit = new BoardWindow.CardFocus(first.Id, BoardWindow.CardAction.Edit);
        board.FocusedCard = edit;
        tray.Send(TrayCommand.Capture, board.Handle);
        capture.Input = "Draft retained across storage failure";
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using (var blocker = new SqliteConnection(connectionString))
        {
            blocker.Open();
            using var lockDatabase = blocker.BeginTransaction();
            Check(!capture.Save(capture.Input), "locked database reports failed capture save");
            var captures = capture.Shows;
            var warnings = WarningWindow.Shows;
            tray.Send(TrayCommand.Preview);
            tray.Send(TrayCommand.PreviewSampleBoard);
            board.FocusedCard = null;
            tray.Send(TrayCommand.Capture, board.Handle);
            Check(capture.IsVisible && capture.Input == "Draft retained across storage failure" && capture.Shows == captures
                && WarningWindow.Shows == warnings && !queue.Timer.Running,
                "failed draft survives both preview commands and capture reinvocation");
            lockDatabase.Rollback();
        }

        Check(capture.Save(capture.Input), "same draft saves on retry");
        Check(!capture.IsVisible && tray.PreviewAvailable && SurfaceWindow.Returned == board.Handle && board.Refreshes == 1,
            "successful save returns to board and refreshes it");
        Check(board.LastRefreshFocus == edit, "retry after a failed save retains the original Edit target");
        Check(new TaskStore(path).List(archived: false).Count == 2, "capture commits once and survives a new store instance");

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
        Check(BreakWindow.Text == capture.Input, "archived In progress task falls back to first To do");
        tray.Send(TrayCommand.EndBreak);
        foreach (var task in store.List(archived: false))
        {
            store.Move(task.Id, TaskStatus.Done);
        }
        Preview();
        Check(BreakWindow.Text is null, "Done-only board shows timer-only break");
        tray.Send(TrayCommand.EndBreak);

        tray.Send(TrayCommand.Preview);
        shownBreaks = BreakWindow.Shows;
        tray.Send(TrayCommand.EndBreak);
        Elapse();
        Check(BreakWindow.Shows == shownBreaks && !queue.Timer.Running, "cancelled warning never opens break");

        CheckCaptureFocus(tray, board, capture, store.List(archived: false).Single().Id);

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
        var enabled = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5, TimerOnly = true };

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
        Check(BreakWindow.Shows == breaks + 1 && BreakWindow.Text is null, "scheduled timer-only break omits the saved task");
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
        var original = ReminderSettings.Default with { Enabled = true, WorkSeconds = 64, TimerOnly = true };
        var store = new TaskStore(path);
        store.SaveReminders(original, new ReminderScheduler(clock, original).Checkpoint());
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
        tray.Send(TrayCommand.Quit);
    }
}
