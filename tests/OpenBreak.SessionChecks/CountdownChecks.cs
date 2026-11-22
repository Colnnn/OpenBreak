using Microsoft.UI.Dispatching;

namespace OpenBreak;

internal static class CountdownChecks
{
    internal static void Run(string path)
    {
        CheckSession(path);
        CheckFormattingAndRaster();
        CheckIconOwnership();
    }

    private static void CheckSession(string path)
    {
        var store = new TaskStore(path);
        var clock = new TestClock(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 125, ShortSeconds = 15 };
        store.SaveReminders(settings, new ReminderScheduler(clock, settings).Checkpoint());
        var queue = new DispatcherQueue();
        FloatingTimerWindow.Current = null;
        var session = new OpenBreakSession(queue, () => { }, store, clock);
        session.RevealFromLaunch();
        var tray = Desktop.Current;
        var host = MainWindow.Current;
        var focus = host.FocusHome;
        void Tick(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                queue.Timers[1].Fire();
            }
        }

        Check(tray.Countdown is null && FloatingTimerWindow.Current is null && tray.FloatingTimerHidden is null,
            "default appearance leaves the tray logo and creates no floating timer");
        host.Navigate(ManagementPage.Appearance);
        var form = AppearanceSettingsPage.Current;
        var trayOnly = AppearancePreferences.Default with { TrayCountdown = true };
        Check(form.Save(trayOnly) && tray.Countdown is { Glyphs: "3", Time: "02:05" } && FloatingTimerWindow.Current is null,
            "the tray countdown can be enabled independently");
        var floatingOnly = AppearancePreferences.Default with { FloatingTimer = true, FloatingTimerOnTop = true };
        Check(form.Save(floatingOnly) && tray.Countdown is null && FloatingTimerWindow.Current is
            { IsVisible: true, KeepOnTop: true, Display.Time: "02:05" } floating && tray.FloatingTimerHandle == floating.Handle,
            "the floating countdown and keep-on-top can be enabled independently of the tray");

        var timer = FloatingTimerWindow.Current!;
        Check(form.Save(floatingOnly with { TrayCountdown = true }), "both countdowns can be enabled together");
        host.Navigate(ManagementPage.Focus);
        Tick(65);
        Check(tray.Countdown is { Glyphs: "1", Time: "01:00", FinalMinute: false } &&
            tray.Countdown == timer.Display && focus.Snapshot.Seconds == 60 && queue.Timers.Count == 2,
            "at sixty seconds both countdowns use the same scheduler snapshot without creating a clock");
        Tick(1);
        Check(tray.Countdown is { Glyphs: "59", Time: "00:59", FinalMinute: true } &&
            tray.Countdown == timer.Display && focus.Snapshot.Seconds == 59,
            "below sixty seconds the tray supplies a distinct seconds treatment and both exact displays agree");
        Check(focus.Execute(FocusAction.Pause) is null, "Focus work pause remains available with countdowns enabled");
        Tick(4);
        Check(timer.IsVisible && tray.Countdown is { Glyphs: "II", Time: "00:59", Phase: "Paused · Work interval" } &&
            tray.Countdown == timer.Display, "work pause keeps the exact frozen value without hiding the timer");
        Check(focus.Execute(FocusAction.Resume) is null, "Focus work resume remains available with countdowns enabled");

        timer.SimulateHide();
        Tick(1);
        Check(!timer.IsVisible && tray.FloatingTimerHidden == true && focus.Snapshot.Seconds == 58 &&
            store.LoadAppearance().FloatingTimer && tray.Countdown?.Time == "00:58",
            "hiding the desktop timer leaves reminders and its persisted opt-in running");
        tray.Send(TrayCommand.ToggleFloatingTimer);
        Check(timer.IsVisible && tray.FloatingTimerHidden == false && timer.Display.Time == "00:58",
            "the tray restores the hidden timer at the current countdown");

        var lastSource = tray.WorkSource;
        CaptureWindow.Current.BeforeShow = () => Check(!timer.IsVisible && tray.WorkSource == lastSource,
            "floating hides before capture opens and leaves the work source intact");
        tray.Send(TrayCommand.Capture, 807);
        CaptureWindow.Current.Cancel();
        Check(timer.IsVisible && SurfaceWindow.Returned == 807, "capture dismiss restores the original source while floating recovers");

        tray.SystemPause = ReminderPause.Suspended;
        tray.ChangeEligibility("power-change");
        clock.Advance(TimeSpan.FromSeconds(3));
        queue.Timers[1].Fire();
        Check(!timer.IsVisible && tray.Countdown is { Glyphs: "II", Time: "00:58" },
            "Windows suspend hides the timer and preserves the paused countdown");
        tray.SystemPause = ReminderPause.Locked;
        tray.ChangeEligibility();
        Check(!timer.IsVisible, "Windows lock keeps the floating countdown hidden");
        tray.SystemPause = ReminderPause.Presentation;
        tray.ChangeEligibility();
        Check(!timer.IsVisible, "Windows presentation keeps the floating countdown hidden");
        tray.SystemPause = ReminderPause.None;
        tray.ChangeEligibility();
        Check(timer.IsVisible && tray.Countdown == timer.Display && timer.Display.Time == "00:58",
            "after a brief sleep recovery resumes the authoritative remaining time");
        tray.Send(TrayCommand.TogglePause);
        Check(!timer.IsVisible, "manual presentation pause hides floating countdown");
        tray.Send(TrayCommand.TogglePause);
        tray.ChangeDisplays();
        Check(timer.IsVisible && timer.DisplayChanges == 1, "display changes invalidate and re-present the timer through the session");

        Tick(58);
        Check(tray.Countdown is { Phase: "Break warning", Time: "00:10" } &&
            tray.Countdown == timer.Display && timer.IsVisible, "scheduled warning consumes the same ten-second phase");
        BreakWindow.Current.BeforeShow = () => Check(!timer.IsVisible, "floating hides before fullscreen break entry");
        Tick(10);
        Check(tray.Countdown is { Phase: "Short break", Time: "00:15" } && !timer.IsVisible &&
            focus.Snapshot is { Phase: ReminderPhase.Break, Seconds: 15 }, "tray continues to show exact break phase while floating stays hidden");
        BreakWindow.Current.End();
        Check(timer.IsVisible && focus.Execute(FocusAction.Stop) is null && tray.Countdown is
            { Phase: "Reminders off", Glyphs: null, Time.Length: 0 } &&
            timer.Display.Phase == "Reminders off", "stopping reminders restores the logo and presents the stopped desktop state");
        var position = new FloatingTimerPosition(@"\\.\DISPLAY2", 2000, 3050);
        timer.SimulateDrag(position);
        tray.Send(TrayCommand.Quit);
        Check(timer.Closed && !queue.Timers[1].IsRunning, "quit closes floating and stops the existing scheduler timer");

        _ = new OpenBreakSession(new DispatcherQueue(), () => { }, store, clock);
        Check(FloatingTimerWindow.Current is { OriginalPosition: { } loaded, IsVisible: true } && loaded == position,
            "startup restores drag placement and the persisted floating opt-in");
        Desktop.Current.Send(TrayCommand.Quit);
    }

    private static void CheckFormattingAndRaster()
    {
        var snapshot = new FocusSnapshot(true, ReminderPhase.Work, 6001, ReminderPause.None, 0, 30, null, false,
            false, null, 0, FocusAction.None);
        Check(CountdownDisplay.From(snapshot) is { Glyphs: "99+", Time: "1:40:01", FinalMinute: false },
            "long durations cap the tray glyphs while preserving exact hours and seconds");
        Check(CountdownDisplay.From(snapshot with { PreviewPhase = ReminderPhase.Warning, PreviewSeconds = 7,
            Enabled = false, PausedBy = ReminderPause.Interaction }) is { Phase: "Preview warning", Glyphs: "07", Time: "00:07" },
            "a manual preview uses its own Focus snapshot fields while reminders are stopped");

        foreach (var size in new[] { 16, 24, 32, 48 })
        {
            var normal = new TrayIconDesign("59", false, size, 0, 0xFFFFFF);
            var plain = TrayCountdownIcon.Rasterize(normal);
            var marked = TrayCountdownIcon.Rasterize(normal with { FinalMinute = true });
            var overflow = TrayCountdownIcon.Rasterize(normal with { Glyphs = "99+" });
            var inset = Math.Max(1, size / 12);
            var marker = (size - 2 * inset) * size + size / 2;
            Check(plain.Length == size * size && plain[marker] != -1 && marked[marker] == -1 &&
                overflow.Contains(-1) && plain[0] == 0 && marked[^1] == 0,
                $"tray raster preserves final-minute underline and large-duration glyphs at {size} pixels");
        }
    }

    private static void CheckIconOwnership()
    {
        List<nint> destroyed = [];
        var next = 20;
        using var lifetime = new TrayIconLifetime(10, _ => next++, destroyed.Add);
        var a = new TrayIconDesign("2", false, 16, 0, 0xFFFFFF);
        var b = a with { Glyphs = "1" };
        var calls = new List<(nint Icon, string Tip, bool IconChanged, bool TipChanged)>();
        var shellAccepts = true;
        bool Publish(nint icon, string tip, bool iconChanged, bool tipChanged)
        {
            calls.Add((icon, tip, iconChanged, tipChanged));
            return shellAccepts;
        }

        Check(lifetime.Apply(null, "Shortcut", true, Publish) && calls is [(10, "Shortcut", true, true)],
            "initial registration uses the shared logo");
        Check(lifetime.Apply(a, "01:51", false, Publish) && lifetime.Apply(a, "01:51", false, Publish) &&
            lifetime.Apply(a, "01:50", false, Publish) && calls.Count == 3 && calls[^1] == (20, "01:50", false, true) && next == 21,
            "unchanged output issues no shell call and a seconds-only tooltip change reuses the minute HICON");
        shellAccepts = false;
        Check(!lifetime.Apply(b, "01:00", false, Publish) && destroyed.SequenceEqual([(nint)21]),
            "failed icon replacement discards the new handle and retains the published handle");
        Check(!lifetime.Apply(a, "01:50", true, Publish) && calls[^1] == (20, "01:50", true, true) &&
            destroyed.Count == 1, "a failed Explorer re-add preserves the current HICON for the retry");
        shellAccepts = true;
        Check(lifetime.Apply(a, "01:50", true, Publish) && calls[^1] == (20, "01:50", true, true) && next == 22,
            "retrying after Explorer restart republishes the current icon and tooltip without rasterizing");
        Check(lifetime.Apply(b, "01:00", false, Publish) && destroyed.SequenceEqual([(nint)21, 20]),
            "a successful icon replacement releases the previous owned handle");
        lifetime.Dispose();
        Check(destroyed.SequenceEqual([(nint)21, 20, 22]), "dispose releases the final owned handle without destroying the shared logo");
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
