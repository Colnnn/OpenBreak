using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace Afterbreak;

internal sealed partial class AfterbreakSession
{
    private static readonly TimeSpan PreviewLength = TimeSpan.FromSeconds(10);
    private readonly Desktop desktop;
    private readonly CaptureWindow capture;
    private readonly WarningWindow warning;
    private readonly BreakWindow preview;
    private readonly DispatcherQueueTimer timer;
    private readonly Action exit;
    private readonly TaskStore tasks;
    private readonly CaptureOptions captureOptions;
    private readonly WindowAppearance appearance;
    private readonly BreakDiagnostics? diagnostics;
    private MainWindow? main;
    private AppearancePreferences appearancePreferences = AppearancePreferences.Default;
    private bool appearanceLoadFailed;
    private Surface visible;
    private bool samplePreview;
    private long startedAt;
    private long diagnosticStartedAt;
    private TimeSpan diagnosticExpected;
    private nint returnWindow;
    private MainWindow.FocusBookmark? captureFocus;

    internal AfterbreakSession(DispatcherQueue dispatcher, Action exit, TaskStore? tasks = null,
        TimeProvider? reminderClock = null, bool captureMotionStudy = false, BreakDiagnostics? diagnostics = null)
    {
        this.exit = exit;
        this.diagnostics = diagnostics;
        this.tasks = tasks ?? TaskStore.ForCurrentUser();
        capture = new CaptureWindow(SaveTask, () => Dismiss(), captureMotionStudy);
        warning = new WarningWindow(SnoozeReminder, SkipReminder);
        preview = new BreakWindow(EndVisibleBreak);
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += Tick;

        desktop = new Desktop(dispatcher);
        desktop.Command += (command, source) => Execute(command, source);
        captureOptions = new CaptureOptions(this.tasks, desktop.CaptureBinding);
        captureOptions.Load();
        desktop.RefreshShortcutTip();
        LoadAppearance();
        appearance = new WindowAppearance(dispatcher, capture, appearancePreferences);
        appearance.Track(capture, SurfaceMaterial.Capture);
        appearance.Track(warning, SurfaceMaterial.Warning);
        appearance.Track(preview, SurfaceMaterial.Break);
        desktop.ConfigureTrayFlyout(appearance, BindTrayCommand);
        InitializeReminders(dispatcher, reminderClock ?? new ReminderClock());
    }

    private enum Surface
    {
        None,
        Capture,
        Warning,
        Preview,
    }

    private readonly record struct CaptureBookmark(MainWindow.FocusBookmark? Focus);

    private Action<TrayCommand> BindTrayCommand(nint source)
    {
        var bookmark = new CaptureBookmark(FocusedMainPage(source));
        return command => Execute(command, source, bookmark);
    }

    private void Execute(TrayCommand command, nint source, CaptureBookmark? bookmark = null)
    {
        switch (command)
        {
            case TrayCommand.OpenMain:
                OpenMain(source);
                break;
            case TrayCommand.Appearance:
                OpenMain(source, ManagementPage.Appearance);
                break;
            case TrayCommand.CaptureSettings:
                OpenMain(source, ManagementPage.Capture);
                break;
            case TrayCommand.Settings:
                OpenMain(source, ManagementPage.Breaks);
                break;
            case TrayCommand.TogglePause:
                TogglePresentationPause();
                break;
            case TrayCommand.Snooze:
                SnoozeReminder();
                break;
            case TrayCommand.Skip:
                SkipReminder();
                break;
            case TrayCommand.Tasks:
                OpenMain(source, ManagementPage.Tasks);
                break;
            case TrayCommand.Capture:
                desktop.DismissTray();
                if (visible == Surface.Capture)
                {
                    var previousSource = returnWindow;
                    var previousFocus = captureFocus;
                    if (source != 0 && (source != returnWindow || (main is { } host && source == host.Handle &&
                        (host.HasForeground || (bookmark?.Focus is { } current && current.Page != captureFocus?.Page)))))
                    {
                        SetCaptureSource(source, bookmark);
                    }
                    if (!capture.BringBack(returnWindow))
                    {
                        returnWindow = previousSource;
                        captureFocus = previousFocus;
                    }
                    return;
                }

                StopScheduledSurfaceForInput();
                Dismiss("capture-command");
                SetCaptureSource(source, bookmark);
                if (!capture.TryShow(source))
                {
                    Dismiss();
                    return;
                }
                visible = Surface.Capture;
                desktop.PreviewAvailable = false;
                capture.StartCaptureEntrance();
                break;
            case TrayCommand.Preview:
            case TrayCommand.PreviewSampleBoard:
                if (visible != Surface.None || !ManualPreviewAllowed(checkPresentation: true))
                {
                    return;
                }

                Dismiss();
                if (!warning.TryShow(source))
                {
                    diagnostics?.Write("event=not-shown phase=Warning mode=manual reason=warning-placement");
                    return;
                }
                BeginManualPreview(source, sample: command == TrayCommand.PreviewSampleBoard);
                break;
            case TrayCommand.EndBreak:
                EndVisibleBreak();
                break;
            case TrayCommand.Quit:
                reminderTimer.Stop();
                if (reminders.Settings.Enabled && !reminderStorageError)
                {
                    PersistCheckpoint();
                }
                Dismiss("quit");
                appearance.Dispose();
                desktop.Dispose();
                capture.Close();
                warning.Close();
                preview.CloseForQuit();
                main?.CloseForQuit();
                exit();
                break;
        }
    }

    private void OpenMain(nint source, ManagementPage? page = null)
    {
        // An explicit reveal can interrupt the display without losing scheduled break time.
        DismissBreakForSettings();
        ShowMain(source, page, activate: true);
    }

    internal void RevealFromLaunch() =>
        ShowMain(desktop.WorkWindow(), page: null, activate: visible is not (Surface.Capture or Surface.Preview));

    private void ShowMain(nint source, ManagementPage? page, bool activate)
    {
        if (main is null)
        {
            var tasksPage = new TasksPage(tasks, () => Execute(TrayCommand.Capture, desktop.MainHandle));
            var breaksPage = new ReminderSettingsPage(SaveSettings);
            var capturePage = new CaptureSettingsPage(ApplyCaptureShortcut, captureOptions.DismissGuide, SaveAndPreviewGuide);
            var appearancePage = new AppearanceSettingsPage(SaveAppearance);
            breaksPage.SetForm(reminders.Settings, reminderStorageError);
            breaksPage.SetStatus(desktop.ReminderStatus);
            capturePage.SetPreferences(captureOptions.Preferences, captureOptions.Status, reminders.Settings.BreakDisplay);
            appearancePage.SetPreferences(appearancePreferences, appearanceLoadFailed);
            main = new MainWindow(tasksPage, breaksPage, capturePage, appearancePage);
            main.PageVisited += RefreshPageAfterLoadFailure;
            desktop.MainHandle = main.Handle;
            appearance.Track(main, SurfaceMaterial.Mica);
        }
        main.TryShow(source, page, activate);
    }

    private void RefreshPageAfterLoadFailure(ManagementPage page)
    {
        if (main is null)
        {
            return;
        }
        switch (page)
        {
            case ManagementPage.Breaks when reminderLoadError && !main.Breaks.IsDirty:
                TryLoadReminderSettings();
                main.Breaks.SetForm(reminders.Settings, reminderStorageError);
                main.Capture.RebaseBreakDisplay(reminders.Settings.BreakDisplay);
                break;
            case ManagementPage.Capture when captureOptions.LoadFailed && !main.Capture.IsDirty:
                captureOptions.Load();
                desktop.RefreshShortcutTip();
                main.Capture.SetPreferences(captureOptions.Preferences, captureOptions.Status, reminders.Settings.BreakDisplay);
                break;
            case ManagementPage.Appearance when appearanceLoadFailed && !main.Appearance.IsDirty:
                LoadAppearance();
                appearance.Apply(appearancePreferences);
                main.Appearance.SetPreferences(appearancePreferences, appearanceLoadFailed);
                break;
        }
    }

    private string? SaveAndPreviewGuide(BreakDisplay display)
    {
        if (main is not { GuideHasForeground: true } guide || visible != Surface.None)
        {
            return "Finish the current capture or preview before starting another preview.";
        }
        if (!Enum.IsDefined(display))
        {
            return "Choose a break display.";
        }
        if (reminderLoadError)
        {
            return "Saved reminder settings are unavailable. Reopen Breaks to retry.";
        }
        if (!ManualPreviewAllowed(checkPresentation: true))
        {
            return "A preview is unavailable while breaks are paused or the display is unavailable.";
        }
        var source = guide.GuidePreviewTarget;
        if (!warning.TryPrepare(source))
        {
            return "The preview warning couldn’t be positioned. Try again.";
        }

        try
        {
            tasks.SaveBreakDisplay(display, reminders.Settings, reminders.Checkpoint());
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            return "Couldn’t save the break display. Try again.";
        }

        reminders = reminders.WithSettings(reminders.Settings with { BreakDisplay = display });
        guide.Breaks.RebaseBreakDisplay(display);
        guide.Capture.RebaseBreakDisplay(display);
        if (!ManualPreviewAllowed(checkPresentation: true) || !guide.GuideHasForeground ||
            !warning.PlacementIsCurrent)
        {
            return "Break display saved. The preview is unavailable right now. Try again.";
        }

        warning.ShowPrepared();
        if (!warning.AppWindow.IsVisible || !warning.PlacementIsCurrent || !guide.GuideHasForeground)
        {
            warning.AppWindow.Hide();
            return "Break display saved. The warning couldn’t open. Try again.";
        }
        BeginManualPreview(source, sample: false);
        guide.HandOffGuidePreview(source);
        return null;
    }

    private void BeginManualPreview(nint source, bool sample)
    {
        samplePreview = sample;
        returnWindow = source;
        visible = Surface.Warning;
        desktop.PreviewAvailable = false;
        desktop.DismissLabel = "Cancel preview";
        StartClock();
    }

    private void LoadAppearance()
    {
        try
        {
            appearancePreferences = tasks.LoadAppearance();
            appearanceLoadFailed = false;
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            appearanceLoadFailed = true;
        }
    }

    private bool SaveAppearance(AppearancePreferences preferences)
    {
        if (appearanceLoadFailed || !preferences.IsValid)
        {
            return false;
        }

        try
        {
            tasks.SaveAppearance(preferences);
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            return false;
        }
        appearancePreferences = preferences;
        appearance.Apply(preferences);
        return true;
    }

    private void DismissBreakForSettings()
    {
        if (scheduledSurface)
        {
            reminders.Update(ReminderPause.Interaction);
            checkpointDirty = true;
        }
        if (visible is Surface.Warning or Surface.Preview)
        {
            Dismiss();
        }
    }

    private string? ApplyCaptureShortcut(CaptureShortcut shortcut)
    {
        var failure = captureOptions.Apply(shortcut);
        desktop.RefreshShortcutTip();
        main?.Capture.SetStatus(captureOptions.Status);
        return failure;
    }

    private void StartClock()
    {
        startedAt = Stopwatch.GetTimestamp();
        timer.Start();
        TraceBreakShown(PreviewLength);
    }

    private void TraceBreakShown(TimeSpan expected)
    {
        if (diagnostics is null)
        {
            return;
        }
        diagnosticStartedAt = Stopwatch.GetTimestamp();
        diagnosticExpected = expected;
        TraceBreak("shown");
    }

    private void TraceBreak(string kind, string? reason = null)
    {
        if (diagnostics is null || visible is not (Surface.Warning or Surface.Preview))
        {
            return;
        }
        var mode = scheduledSurface ? "scheduled" : samplePreview ? "sample" : "manual";
        var elapsed = Stopwatch.GetElapsedTime(diagnosticStartedAt);
        var placement = visible == Surface.Warning ? warning.DescribePlacement() : preview.DescribePlacement();
        diagnostics.Write(FormattableString.Invariant(
            $"event={kind} phase={visible} mode={mode} reason={reason ?? "none"} elapsedMs={elapsed.TotalMilliseconds:F0} expectedMs={diagnosticExpected.TotalMilliseconds:F0} foreground={preview.HasForeground} trayForeground={desktop.TrayFlyoutHasForeground} manualPause={reminders.ManuallyPaused} {desktop.LastBreakEligibility} {placement}"));
    }

    private MainWindow.FocusBookmark? FocusedMainPage(nint source) =>
        main is not null && source == main.Handle ? main.GetFocusBookmark() : null;

    private void SetCaptureSource(nint source, CaptureBookmark? bookmark)
    {
        returnWindow = source;
        captureFocus = bookmark is { } captured ? captured.Focus : FocusedMainPage(source);
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        if (scheduledSurface || visible is not (Surface.Warning or Surface.Preview))
        {
            return;
        }
        if (EndIfPlacementInvalid("manual-tick"))
        {
            return;
        }
        if (!ManualPreviewAllowed(checkPresentation: visible != Surface.Preview ||
            (!preview.HasForeground && !desktop.TrayFlyoutHasForeground)))
        {
            Dismiss("manual-tick-eligibility");
            return;
        }
        if (desktop.MenuOpen)
        {
            return;
        }

        var remaining = PreviewLength - Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero)
        {
            var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            if (visible == Surface.Warning)
            {
                warning.Update(seconds);
            }
            else if (visible == Surface.Preview)
            {
                preview.Update(seconds);
            }
            return;
        }

        timer.Stop();
        if (visible == Surface.Warning)
        {
            warning.AppWindow.Hide();
            preview.SetPreview(ReadBreakView(samplePreview));
            if (!ManualPreviewAllowed(checkPresentation: true) || !warning.PlacementIsCurrent)
            {
                Dismiss("manual-before-break-eligibility-or-warning-placement");
                return;
            }
            var source = desktop.WorkWindow();
            if (!preview.TryShow(source))
            {
                diagnostics?.Write($"event=not-shown phase=Preview mode=manual reason=prepare-break {preview.DescribePlacement()}");
                Dismiss("manual-break-placement");
                return;
            }
            returnWindow = source;
            visible = Surface.Preview;
            desktop.DismissLabel = "End break";
            StartClock();
        }
        else if (visible == Surface.Preview)
        {
            Dismiss("manual-tick-expired");
        }
    }

    private BreakView ReadBreakView(bool sample = false)
    {
        if (sample)
        {
            return new BreakView(BreakLayout.SampleBoard);
        }

        try
        {
            switch (reminders.Settings.BreakDisplay)
            {
                case BreakDisplay.SavedTaskBoard:
                    var saved = tasks.List(archived: false);
                    if (saved.Count > 0)
                    {
                        return new BreakView(BreakLayout.SavedBoard, Tasks: saved);
                    }
                    break;
                case BreakDisplay.OneTask:
                    if (tasks.ReturnTask() is { } task)
                    {
                        return new BreakView(BreakLayout.Compact, Task: task);
                    }
                    break;
            }
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            // The timer can still run when saved task content is unavailable.
        }
        return new BreakView(BreakLayout.TimerOnly);
    }

    private bool SaveTask(string title, string? nextStep)
    {
        if (visible != Surface.Capture)
        {
            return false;
        }

        try
        {
            tasks.Add(title, nextStep);
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception) || exception is ArgumentException)
        {
            return false;
        }

        var focus = captureFocus;
        Dismiss();
        main?.NotifyTaskCaptured(focus);
        return true;
    }

    private void Dismiss([CallerMemberName] string reason = "")
    {
        TraceBreak("dismiss", reason);
        var closing = visible;
        scheduledSurface = false;
        visible = Surface.None;
        timer.Stop();
        desktop.DismissLabel = null;
        desktop.PreviewAvailable = true;
        switch (closing)
        {
            case Surface.Capture:
                capture.StopCaptureEntrance();
                SurfaceWindow.HideAndReturn(capture, returnWindow);
                break;
            case Surface.Warning:
                warning.AppWindow.Hide();
                break;
            case Surface.Preview:
                preview.HideAndReturn(returnWindow);
                break;
        }
        returnWindow = 0;
        captureFocus = null;
        desktop.RefreshTrayFlyout();
    }
}
