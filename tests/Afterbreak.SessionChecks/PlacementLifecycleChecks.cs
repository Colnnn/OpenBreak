using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Dispatching;

namespace Afterbreak;

internal static class PlacementLifecycleChecks
{
    internal static void Run(string directory)
    {
        Manual(Path.Combine(directory, "manual-displays.db"));
        Scheduled(Path.Combine(directory, "scheduled-displays.db"));
        DiagnosticTrace(directory);
    }

    private static void Manual(string path)
    {
        var queue = new DispatcherQueue();
        var session = new AfterbreakSession(queue, () => { }, new TaskStore(path));
        var tray = Desktop.Current;
        var capture = CaptureWindow.Current;
        var warning = WarningWindow.Current;
        var overlay = BreakWindow.Current;
        var startedAt = typeof(AfterbreakSession).GetField("startedAt", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Check the preview clock before updating this test.");

        void ElapsePreview()
        {
            startedAt.SetValue(session, Stopwatch.GetTimestamp() - 11 * Stopwatch.Frequency);
            queue.Timer.Fire();
        }

        capture.CanPlace = false;
        tray.Send(TrayCommand.Capture, 40);
        Check(!capture.IsVisible && tray.PreviewAvailable, "failed capture placement does not leave a hidden capture session");
        capture.CanPlace = true;
        tray.Send(TrayCommand.Capture, 40);
        capture.Input = "Pending capture draft";
        capture.CanPlace = false;
        tray.Send(TrayCommand.Capture, 50);
        Check(capture.IsVisible && capture.Input == "Pending capture draft", "a failed capture reinvocation leaves its draft intact");
        capture.Cancel();
        Check(SurfaceWindow.Returned == 40, "a failed reinvocation retains the original focus return source");

        warning.CanPlace = false;
        tray.Send(TrayCommand.PreviewSampleBoard);
        Check(!warning.IsVisible && tray.DismissLabel is null && tray.PreviewAvailable && !queue.Timer.Running,
            "failed manual warning placement does not start its preview clock");
        warning.CanPlace = true;
        tray.Send(TrayCommand.Preview);
        tray.ChangeDisplays();
        Check(warning.IsVisible && queue.Timer.Running, "unrelated display events leave a valid manual warning visible");
        warning.PlacementIsCurrent = false;
        tray.ChangeDisplays();
        tray.ChangeDisplays();
        Check(!warning.IsVisible && !queue.Timer.Running && overlay.Attempts == 0, "loss of the warning display quietly cancels a manual preview");

        tray.Send(TrayCommand.Preview);
        warning.Hide();
        ElapsePreview();
        Check(!queue.Timer.Running && overlay.Attempts == 0, "manual timing notices a warning hidden by failed reflow without a display event");

        overlay.CanPlace = false;
        tray.Send(TrayCommand.Preview);
        ElapsePreview();
        Check(!overlay.IsVisible && overlay.Attempts == 1 && !queue.Timer.Running && tray.DismissLabel is null,
            "failed manual break placement does not show an overlay or leave the clock running");

        overlay.CanPlace = true;
        tray.Send(TrayCommand.Preview);
        ElapsePreview();
        tray.ChangeDisplays();
        Check(overlay.IsVisible && queue.Timer.Running, "an unrelated display event does not dismiss a manual break");
        var returns = SurfaceWindow.Returns;
        overlay.RetainedDisplayAvailable = false;
        tray.ChangeDisplays();
        tray.ChangeDisplays();
        Check(!overlay.IsVisible && SurfaceWindow.Returns == returns + 1 && !queue.Timer.Running,
            "target removal dismisses a manual break and returns focus once");
        tray.Send(TrayCommand.Quit);
    }

    private static void DiagnosticTrace(string directory)
    {
        var path = Path.Combine(directory, "preview-diagnostics.log");
        Check(BreakDiagnostics.Open([]) is null && BreakDiagnostics.Open(["--diagnostics", "relative.log"]) is null &&
            !File.Exists(path), "break diagnostics are opt-in and reject relative paths");

        using var diagnostics = BreakDiagnostics.Open(["--diagnostics", path]) ??
            throw new InvalidOperationException("Could not open the isolated trace.");
        var store = new TaskStore(Path.Combine(directory, "diagnostic-preview.db"));
        store.Add("Private diagnostic fixture task title");
        var queue = new DispatcherQueue();
        var session = new AfterbreakSession(queue, () => { }, store, diagnostics: diagnostics);
        var tray = Desktop.Current;
        tray.Send(TrayCommand.Preview);
        var startedAt = typeof(AfterbreakSession).GetField("startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        startedAt.SetValue(session, Stopwatch.GetTimestamp() - 11 * Stopwatch.Frequency);
        queue.Timer.Fire();
        tray.CheckSystemPause = () => tray.LastPresentationCheck ? ReminderPause.Presentation : ReminderPause.None;
        tray.ChangeEligibility("settings-change");
        Check(!BreakWindow.Current.IsVisible, "diagnostic mode preserves existing automatic eligibility dismissal");
        tray.Send(TrayCommand.Quit);

        using var reader = new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var entries = reader.ReadToEnd();
        Check(entries.Contains("event=shown phase=Warning mode=manual", StringComparison.Ordinal) &&
            entries.Contains("event=shown phase=Preview mode=manual", StringComparison.Ordinal) &&
            entries.Contains("event=dismiss phase=Preview mode=manual reason=settings-change-eligibility", StringComparison.Ordinal) &&
            entries.Contains("expectedMs=10000 foreground=True", StringComparison.Ordinal) &&
            entries.Contains("shellState=2", StringComparison.Ordinal) &&
            !entries.Contains("Private diagnostic fixture task title", StringComparison.Ordinal),
            "the diagnostic trace distinguishes the source and decision without logging task content");
        Check(BreakDiagnostics.Open(["--diagnostics", path]) is null,
            "a second diagnostic run cannot replace an existing log or fail app startup");
        Check(BreakDiagnostics.Open(["--diagnostics", path + ":alternate.log"]) is null,
            "diagnostics cannot add an alternate data stream to an existing file");
    }

    private static void Scheduled(string path)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 5, ShortSeconds = 10 };
        var store = new TaskStore(path);
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var queue = new DispatcherQueue();
        _ = new AfterbreakSession(queue, () => { }, store, clock);
        var tray = Desktop.Current;
        var warning = WarningWindow.Current;
        var overlay = BreakWindow.Current;

        void Tick(int seconds)
        {
            for (var second = 0; second < seconds; second++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                queue.Timers[1].Fire();
            }
        }

        bool FreshWork() => store.LoadReminders().Checkpoint is
            { Phase: ReminderPhase.Work, CompletedShorts: 0 } current && current.Remaining == settings.WorkLength;

        warning.CanPlace = false;
        Tick(5);
        Check(FreshWork() && warning.Attempts == 1 && !warning.IsVisible && !tray.SnoozeAvailable,
            "scheduled warning placement failure saves fresh work without starting an invisible warning");
        Tick(4);
        Check(warning.Attempts == 1, "the scheduler does not immediately retry a failed warning");
        warning.CanPlace = true;
        overlay.CanPlace = false;
        Tick(11);
        Check(overlay.Attempts == 1 && !overlay.IsVisible && FreshWork(), "scheduled break placement failure never credits a break");

        overlay.CanPlace = true;
        Tick(14);
        var eligibilityReads = 0;
        tray.CheckSystemPause = () =>
        {
            if (++eligibilityReads == 2)
            {
                warning.PlacementIsCurrent = false;
            }
            return ReminderPause.None;
        };
        Tick(1);
        tray.CheckSystemPause = null;
        Check(overlay.Attempts == 1 && FreshWork(), "display loss during final scheduled eligibility avoids an overlay on another monitor");

        Tick(5);
        tray.ChangeDisplays();
        Check(warning.IsVisible, "unrelated display changes leave a scheduled warning alone");
        warning.Hide();
        Tick(1);
        Check(overlay.Attempts == 1 && FreshWork(), "a scheduled warning hidden by failed reflow cannot advance to break");

        Tick(15);
        tray.ChangeDisplays();
        Check(overlay.IsVisible, "unrelated display changes leave a scheduled overlay alone");
        var returns = SurfaceWindow.Returns;
        overlay.RetainedDisplayAvailable = false;
        // The timed check is the fallback for a queued or missing display event.
        Tick(1);
        tray.ChangeDisplays();
        tray.ChangeDisplays();
        Check(!overlay.IsVisible && SurfaceWindow.Returns == returns + 1 && FreshWork(),
            "scheduled display removal ends once, saves fresh work and advances no cadence");
        tray.Send(TrayCommand.Quit);
    }

    private static void Check(bool passed, string message)
    {
        if (!passed)
        {
            throw new InvalidOperationException(message);
        }
        Console.WriteLine($"PASS {message}");
    }
}
