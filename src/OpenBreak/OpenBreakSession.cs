using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace OpenBreak;

internal sealed partial class OpenBreakSession
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
    private readonly Action? playBreakCue;
    private MainWindow? main;
    private FloatingTimerWindow? floatingTimer;
    private AppearancePreferences appearancePreferences = AppearancePreferences.Default;
    private bool appearanceLoadFailed;
    private bool floatingTimerHidden;
    private bool quitting;
    private Surface visible;
    private bool samplePreview;
    private long startedAt;
    private long diagnosticStartedAt;
    private TimeSpan diagnosticExpected;
    private nint returnWindow;
    private MainWindow.FocusBookmark? captureFocus;

    internal OpenBreakSession(DispatcherQueue dispatcher, Action exit, TaskStore? tasks = null,
        TimeProvider? reminderClock = null, bool captureMotionStudy = false, BreakDiagnostics? diagnostics = null,
        Action? playBreakCue = null, Func<LastInputSample?>? readLastInput = null)
    {
        this.exit = exit;
        this.diagnostics = diagnostics;
        this.playBreakCue = playBreakCue;
        this.readLastInput = readLastInput ?? LastInputActivity.Read;
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
        desktop.SetTrayAppearance(appearance.State);
        appearance.Changed += desktop.SetTrayAppearance;
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
            case TrayCommand.ToggleFloatingTimer:
                SetFloatingTimerHidden(!floatingTimerHidden);
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

                floatingTimer?.Hide();
                StopScheduledSurfaceForInput();
                Dismiss("capture-command", includeFloating: false);
                SetCaptureSource(source, bookmark);
                if (!capture.TryShow(source))
                {
                    Dismiss();
                    return;
                }
                visible = Surface.Capture;
                desktop.PreviewAvailable = false;
                capture.StartCaptureEntrance();
                PublishFocusSnapshot();
                break;
            case TrayCommand.Preview:
            case TrayCommand.PreviewSampleBoard:
                TryStartManualPreview(source, sample: command == TrayCommand.PreviewSampleBoard);
                break;
            case TrayCommand.EndBreak:
                EndVisibleBreak();
                break;
            case TrayCommand.Quit:
                quitting = true;
                floatingTimer?.CloseForQuit();
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
            var focusPage = new FocusPage(ExecuteFocusAction, page => main?.Navigate(page), () => main?.OpenGettingStarted());
            focusPage.SetActionError(reminderActionError);
            var tasksPage = new TasksPage(tasks);
            var breaksPage = new ReminderSettingsPage(SaveSettings);
            var capturePage = new CaptureSettingsPage(ApplyCaptureShortcut, captureOptions.DismissGuide,
                SaveAndPreviewGuide, page => main?.Navigate(page));
            var appearancePage = new AppearanceSettingsPage(SaveAppearance);
            breaksPage.SetForm(reminders.Settings, reminderStorageError);
            breaksPage.SetStatus(desktop.ReminderStatus);
            capturePage.SetPreferences(captureOptions.Preferences, captureOptions.Status, reminders.Settings.BreakDisplay);
            appearancePage.SetPreferences(appearancePreferences, appearanceLoadFailed);
            main = new MainWindow(focusPage, tasksPage, breaksPage, capturePage, appearancePage);
            main.PageVisited += RefreshPageAfterLoadFailure;
            main.QuickCaptureRequested += () => BindTrayCommand(main.Handle)(TrayCommand.Capture);
            desktop.MainHandle = main.Handle;
            appearance.Track(main, SurfaceMaterial.Management);
        }
        main.TryShow(source, page, activate);
        UpdateReminderTimer();
    }

    private void RefreshPageAfterLoadFailure(ManagementPage page)
    {
        if (main is null)
        {
            return;
        }
        switch (page)
        {
            case ManagementPage.Focus:
                RefreshFocusTask();
                break;
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
                ApplyCountdownPreferences();
                break;
        }
        UpdateReminderTimer();
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

    private string? TryStartManualPreview(nint source, bool sample)
    {
        if (visible != Surface.None || !ManualPreviewAllowed(checkPresentation: true))
        {
            return "A break preview is unavailable right now.";
        }

        Dismiss();
        if (!warning.TryShow(source))
        {
            diagnostics?.Write("event=not-shown phase=Warning mode=manual reason=warning-placement");
            return "The preview warning couldn’t open. Try again.";
        }
        BeginManualPreview(source, sample);
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
        PublishFocusSnapshot();
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
        if (appearancePreferences.FloatingTimer != preferences.FloatingTimer)
        {
            floatingTimerHidden = false;
        }
        appearancePreferences = preferences;
        appearance.Apply(preferences);
        ApplyCountdownPreferences();
        return true;
    }

    private bool WantsFloatingTimer => !quitting && appearancePreferences.FloatingTimer && !floatingTimerHidden;

    private void ApplyCountdownPreferences()
    {
        if (!WantsFloatingTimer)
        {
            floatingTimer?.Hide();
        }
        if (!appearancePreferences.TrayCountdown)
        {
            desktop.SetCountdown(null, appearance.State);
        }
        desktop.FloatingTimerHidden = appearancePreferences.FloatingTimer ? floatingTimerHidden : null;
        PublishFocusSnapshot();
        desktop.RefreshTrayFlyout();
        UpdateReminderTimer();
    }

    private void SetFloatingTimerHidden(bool hidden)
    {
        if (appearancePreferences.FloatingTimer)
        {
            floatingTimerHidden = hidden;
            desktop.FloatingTimerHidden = hidden;
            PublishFocusSnapshot();
            desktop.RefreshTrayFlyout();
            UpdateReminderTimer();
        }
    }

    private void PublishCountdownDisplays(FocusSnapshot snapshot, bool includeFloating)
    {
        if (quitting)
        {
            return;
        }
        var display = CountdownDisplay.From(snapshot);
        if (appearancePreferences.TrayCountdown)
        {
            desktop.SetCountdown(display, appearance.State);
        }
        if (!includeFloating || !WantsFloatingTimer ||
            !CountdownDisplay.MayFloat(snapshot, visible is Surface.Capture or Surface.Preview))
        {
            floatingTimer?.Hide();
            return;
        }
        if (floatingTimer is null)
        {
            FloatingTimerPosition? position = null;
            try
            {
                position = tasks.LoadFloatingTimerPosition();
            }
            catch (Exception exception) when (TaskStore.IsStorageError(exception))
            {
                // The default work-area corner remains usable if placement cannot be read.
            }
            floatingTimer = new FloatingTimerWindow(() => SetFloatingTimerHidden(true), SaveFloatingTimerPosition, position);
            desktop.FloatingTimerHandle = floatingTimer.Handle;
            appearance.Track(floatingTimer, SurfaceMaterial.FloatingTimer);
        }
        // Eligibility can change while reading the saved position and constructing the surface.
        if (floatingTimer.AppWindow.IsVisible || desktop.GetSystemPause(checkPresentation: true) == ReminderPause.None)
        {
            floatingTimer.Present(display, appearancePreferences.FloatingTimerOnTop);
        }
    }

    private void SaveFloatingTimerPosition(FloatingTimerPosition position)
    {
        try
        {
            tasks.SaveFloatingTimerPosition(position);
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            // Keep the session position. The last valid saved placement remains on disk.
        }
    }

    private void PlayBreakStartCue()
    {
        if (!appearanceLoadFailed && appearancePreferences.BreakStartSound)
        {
            playBreakCue?.Invoke();
        }
    }

    private void DismissBreakForSettings()
    {
        if (scheduledSurface)
        {
            reminders.Update(ReminderPause.Interaction, stagingBreak: IsPreparingScheduledBreak);
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
            if (appearancePreferences.TrayCountdown)
            {
                PublishFocusSnapshot();
            }
            return;
        }

        var remaining = ManualPreviewRemaining;
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
            PublishFocusSnapshot();
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
            floatingTimer?.Hide();
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
            PlayBreakStartCue();
            PublishFocusSnapshot();
        }
        else if (visible == Surface.Preview)
        {
            Dismiss("manual-tick-expired");
        }
    }

    private TimeSpan ManualPreviewRemaining => PreviewLength - Stopwatch.GetElapsedTime(startedAt);

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
        if (main?.CurrentPage == ManagementPage.Focus)
        {
            RefreshFocusTask();
        }
        return true;
    }

    private void Dismiss([CallerMemberName] string reason = "", bool includeFloating = true)
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
        PublishFocusSnapshot(includeFloating);
        desktop.RefreshTrayFlyout();
    }
}
