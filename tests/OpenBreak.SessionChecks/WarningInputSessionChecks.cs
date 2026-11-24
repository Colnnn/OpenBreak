using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;

namespace OpenBreak;

internal static class WarningInputSessionChecks
{
    internal static void Run(string path)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskStore(path);
        store.Add("Return task");
        store.SaveAppearance(AppearancePreferences.Default with { BreakStartSound = true });
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5, ShortSeconds = 10, FinishTyping = true };
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var queue = new DispatcherQueue();
        var inputsRead = 0;
        var cues = 0;
        uint input = 0;
        uint NowTick() => 100_000 + (uint)(clock.GetTimestamp() / TimeSpan.TicksPerMillisecond);
        var session = new OpenBreakSession(queue, () => { }, store, clock, playBreakCue: () => cues++, readLastInput: () =>
        {
            inputsRead++;
            return new LastInputSample(NowTick(), input);
        });
        var tray = Desktop.Current;
        var warning = WarningWindow.Current;
        var overlay = BreakWindow.Current;
        var timerCount = queue.Timers.Count;
        var breakShows = BreakWindow.Shows;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var trigger = connection.CreateCommand();
        void Tick(int milliseconds)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            queue.Timers[1].Fire();
        }

        tray.Send(TrayCommand.Settings);
        trigger.CommandText = """
            CREATE TRIGGER reject_input_option BEFORE UPDATE OF finish_typing ON reminder_state
            WHEN NEW.finish_typing = 0 BEGIN SELECT RAISE(IGNORE); END;
            """;
        trigger.ExecuteNonQuery();
        Check(!ReminderSettingsPage.Current.Save(settings with { FinishTyping = false }) && store.LoadReminders().Settings == settings,
            "A failed Finish typing preference save retains the prior selection");
        trigger.CommandText = "DROP TRIGGER reject_input_option;";
        trigger.ExecuteNonQuery();
        var breaks = ReminderSettingsPage.Current;
        Check(breaks.IsDirty, "A rejected Finish typing draft remains available for retry");
        breaks.Discard();
        Check(!breaks.IsDirty && breaks.Draft == settings, "Discard restores the saved input preference");

        tray.Send(TrayCommand.Preview);
        tray.ChangeEligibility();
        Tick(5000);
        var startedAt = typeof(OpenBreakSession).GetField("startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        startedAt.SetValue(session, Stopwatch.GetTimestamp() - 11 * Stopwatch.Frequency);
        queue.Timer.Fire();
        Check(inputsRead == 0 && overlay.IsVisible && BreakWindow.Shows == breakShows + 1,
            "Manual preview with Finish typing enabled follows its normal warning without sampling input");
        overlay.End();
        tray.ChangeEligibility();
        var initialCues = cues;
        var initialShows = BreakWindow.Shows;

        Tick(5000);
        Tick(5000);
        Tick(4000);
        Check(warning.IsVisible && !warning.HoldingForInput && WarningWindow.Seconds == 1 && inputsRead == 1,
            "A quiet scheduled warning reaches its final second without holding");

        overlay.BeforeShow = () =>
        {
            overlay.BeforeShow = null;
            Check(warning.IsVisible && !overlay.IsVisible && BreakWindow.Text is null && BreakWindow.Layout == BreakLayout.SavedBoard,
                "The warning stays visible during task loading and break preparation");
            input = NowTick();
        };
        Tick(1000);
        Check(warning.IsVisible && warning.HoldingForInput && WarningWindow.Seconds == 1 && !overlay.IsVisible &&
            !overlay.ShellFullscreenExcluded && BreakWindow.Shows == initialShows && cues == initialCues &&
            tray.SnoozeAvailable && tray.SkipAvailable && queue.Timers.Count == timerCount && inputsRead == 3,
            "Input during break preparation restores the final second without activation, sound or an additional timer");

        trigger.CommandText = "CREATE TRIGGER reject_snooze BEFORE UPDATE ON reminder_state BEGIN SELECT RAISE(IGNORE); END;";
        trigger.ExecuteNonQuery();
        warning.Snooze();
        var saveError = warning.SaveError;
        trigger.CommandText = "DROP TRIGGER reject_snooze;";
        trigger.ExecuteNonQuery();
        Tick(500);
        Check(saveError is not null && warning.SaveError == saveError && warning.HoldingForInput && !overlay.IsVisible,
            "Hold updates preserve a failed Snooze error and keep the warning actionable");

        Tick(2500);
        Check(!warning.HoldingForInput && warning.IsVisible && WarningWindow.Seconds == 1 && cues == initialCues,
            "Three seconds of quiet releases the warning for a full final second");
        Tick(999);
        Check(warning.IsVisible && !overlay.IsVisible, "The scheduled overlay waits for the whole final second");
        Tick(1);
        Check(!warning.IsVisible && overlay.IsVisible && BreakWindow.Shows == initialShows + 1 && cues == initialCues + 1,
            "The scheduled break appears once with one cue after the final quiet second");

        Tick(4000);
        tray.SystemPause = ReminderPause.Locked;
        tray.ChangeEligibility();
        tray.SystemPause = ReminderPause.None;
        tray.ChangeEligibility();
        Tick(5000);
        Tick(4000);
        overlay.BeforeShow = () =>
        {
            overlay.BeforeShow = null;
            input = NowTick();
        };
        Tick(1000);
        Tick(3000);
        Tick(1000);
        Check(overlay.IsVisible && overlay.Seconds == 6 && BreakWindow.Shows == initialShows + 2 && cues == initialCues + 2,
            "A late input hold on resumption preserves the interrupted break remainder");

        overlay.End();
        Tick(5000);
        Tick(5000);
        input = NowTick();
        Tick(4000);
        input = NowTick();
        Tick(200);
        Check(warning.IsVisible && warning.HoldingForInput, "The next scheduled warning can independently hold");
        tray.Send(TrayCommand.Capture, 301);
        var capture = CaptureWindow.Current;
        Check(capture.IsVisible && !warning.IsVisible && !overlay.IsVisible, "Capture preempts an active warning hold");
        var readsBefore = inputsRead;
        Tick(5000);
        Check(inputsRead == readsBefore && BreakWindow.Shows == initialShows + 2 && cues == initialCues + 2,
            "A stale held warning cannot complete or poll during capture");
        capture.Cancel();
        tray.ChangeEligibility();
        Check(warning.IsVisible && !warning.HoldingForInput && WarningWindow.Seconds == 10,
            "Leaving capture starts a new full warning without the old hold");

        Tick(5000);
        Tick(4000);
        overlay.BeforeShow = () =>
        {
            overlay.BeforeShow = null;
            tray.Send(TrayCommand.Capture, 302);
        };
        var readsBeforePreparation = inputsRead;
        Tick(1000);
        Check(inputsRead == readsBeforePreparation + 1 && capture.IsVisible && !warning.IsVisible && !overlay.IsVisible &&
            !overlay.ShellFullscreenExcluded && BreakWindow.Shows == initialShows + 2 && cues == initialCues + 2,
            "Capture during break preparation cancels the pending show and prevents a stale input read, activation or cue");
        Tick(5000);
        Check(capture.IsVisible && inputsRead == readsBeforePreparation + 1 && BreakWindow.Shows == initialShows + 2,
            "Scheduled ticks do not finish the replaced warning while capture owns the surface");
        capture.Cancel();
        tray.ChangeEligibility();
        Check(warning.IsVisible && !warning.HoldingForInput && WarningWindow.Seconds == 10,
            "Canceling capture after a canceled show starts a fresh warning");

        Tick(5000);
        Tick(4000);
        overlay.BeforeShow = () =>
        {
            overlay.BeforeShow = null;
            clock.Advance(TimeSpan.FromSeconds(6));
        };
        var readsBeforeGap = inputsRead;
        Tick(1000);
        Check(inputsRead == readsBeforeGap + 1 && !overlay.IsVisible && !overlay.ShellFullscreenExcluded &&
            BreakWindow.Shows == initialShows + 2 && cues == initialCues + 2,
            "An unreported six-second preparation gap cancels the staged break before the input read or cue");
        Tick(0);
        Check(warning.IsVisible && !warning.HoldingForInput && WarningWindow.Seconds == 10,
            "The unreported preparation gap restarts a full warning");
        Tick(5000);
        Tick(4000);
        overlay.BeforeShow = () =>
        {
            overlay.BeforeShow = null;
            clock.Advance(TimeSpan.FromMilliseconds(1200));
        };
        Tick(1000);
        Check(overlay.IsVisible && overlay.Seconds == 10 && BreakWindow.Shows == initialShows + 3 && cues == initialCues + 3,
            "An eligible preparation delay spends none of the fresh break duration");
        tray.Send(TrayCommand.Quit);
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
