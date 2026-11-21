using System.Diagnostics;
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
    private BoardWindow? board;
    private CaptureSettingsWindow? captureSettings;
    private AppearanceSettingsWindow? appearanceSettings;
    private AppearancePreferences appearancePreferences = AppearancePreferences.Default;
    private bool appearanceLoadFailed;
    private Surface visible;
    private BreakLayout previewLayout;
    private long startedAt;
    private nint returnWindow;
    private BoardWindow.CardFocus? captureCardFocus;

    internal AfterbreakSession(DispatcherQueue dispatcher, Action exit, TaskStore? tasks = null,
        TimeProvider? reminderClock = null, bool captureMotionStudy = false)
    {
        this.exit = exit;
        this.tasks = tasks ?? TaskStore.ForCurrentUser();
        capture = new CaptureWindow(SaveTask, Dismiss, captureMotionStudy);
        warning = new WarningWindow(SnoozeReminder, SkipReminder);
        preview = new BreakWindow(EndVisibleBreak);
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += Tick;

        desktop = new Desktop(dispatcher);
        desktop.Command += Execute;
        captureOptions = new CaptureOptions(this.tasks, desktop.CaptureBinding);
        captureOptions.Load();
        desktop.RefreshShortcutTip();
        LoadAppearance();
        appearance = new WindowAppearance(dispatcher, capture, appearancePreferences);
        appearance.Track(capture, SurfaceMaterial.Capture);
        appearance.Track(warning, SurfaceMaterial.Warning);
        appearance.Track(preview, SurfaceMaterial.Break);
        InitializeReminders(dispatcher, reminderClock ?? new ReminderClock());
    }

    private enum Surface
    {
        None,
        Capture,
        Warning,
        Preview,
    }

    private void Execute(TrayCommand command, nint source)
    {
        switch (command)
        {
            case TrayCommand.Appearance:
                DismissBreakForSettings(preserveSchedule: true);
                if (appearanceLoadFailed && appearanceSettings?.AppWindow.IsVisible != true)
                {
                    LoadAppearance();
                    appearance.Apply(appearancePreferences);
                }
                if (appearanceSettings is null)
                {
                    appearanceSettings = new AppearanceSettingsWindow(SaveAppearance);
                    appearance.Track(appearanceSettings, SurfaceMaterial.Mica);
                }
                desktop.AppearanceHandle = appearanceSettings.Handle;
                appearanceSettings.Show(source, appearancePreferences, appearanceLoadFailed);
                break;
            case TrayCommand.CaptureSettings:
                DismissBreakForSettings(preserveSchedule: true);
                if (captureOptions.LoadFailed && captureSettings?.AppWindow.IsVisible != true)
                {
                    captureOptions.Load();
                    desktop.RefreshShortcutTip();
                }
                if (captureSettings is null)
                {
                    captureSettings = new CaptureSettingsWindow(ApplyCaptureShortcut, captureOptions.DismissGuide);
                    appearance.Track(captureSettings, SurfaceMaterial.Mica);
                }
                desktop.CaptureSettingsHandle = captureSettings.Handle;
                captureSettings.Show(source, captureOptions.Preferences, captureOptions.Status);
                break;
            case TrayCommand.Settings:
                DismissBreakForSettings();
                if (reminderLoadError && reminderSettings?.AppWindow.IsVisible != true)
                {
                    TryLoadReminderSettings();
                }
                if (reminderSettings is null)
                {
                    reminderSettings = new SettingsWindow(SaveSettings);
                    appearance.Track(reminderSettings, SurfaceMaterial.Mica);
                }
                desktop.SettingsHandle = reminderSettings.Handle;
                reminderSettings.Show(source, reminders.Settings, desktop.ReminderStatus, reminderStorageError);
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
                StopScheduledSurfaceForInput();
                if (visible is Surface.Warning or Surface.Preview)
                {
                    Dismiss();
                }

                if (board is null)
                {
                    board = new BoardWindow(tasks, () => Execute(TrayCommand.Capture, desktop.BoardHandle));
                    appearance.Track(board, SurfaceMaterial.Mica);
                }
                desktop.BoardHandle = board.Handle;
                board.Show(source);
                break;
            case TrayCommand.Capture:
                if (visible == Surface.Capture)
                {
                    var previousSource = returnWindow;
                    var previousCardFocus = captureCardFocus;
                    if (source != 0 && source != returnWindow)
                    {
                        SetCaptureSource(source);
                    }
                    if (!capture.BringBack(returnWindow))
                    {
                        returnWindow = previousSource;
                        captureCardFocus = previousCardFocus;
                    }
                    return;
                }

                StopScheduledSurfaceForInput();
                Dismiss();
                SetCaptureSource(source);
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
                previewLayout = command == TrayCommand.Preview ? BreakLayout.Compact : BreakLayout.SampleBoard;
                if (!warning.TryShow(source))
                {
                    return;
                }
                returnWindow = source;
                visible = Surface.Warning;
                desktop.PreviewAvailable = false;
                desktop.DismissLabel = "Cancel preview";
                StartClock();
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
                Dismiss();
                appearance.Dispose();
                desktop.Dispose();
                capture.Close();
                warning.Close();
                preview.Close();
                board?.CloseForQuit();
                reminderSettings?.CloseForQuit();
                captureSettings?.CloseForQuit();
                appearanceSettings?.CloseForQuit();
                exit();
                break;
        }
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

    private void DismissBreakForSettings(bool preserveSchedule = false)
    {
        if (preserveSchedule && scheduledSurface)
        {
            reminders.Update(ReminderPause.Interaction);
            checkpointDirty = true;
        }
        else
        {
            StopScheduledSurfaceForInput();
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
        captureSettings?.SetStatus(captureOptions.Status);
        return failure;
    }

    private void StartClock()
    {
        startedAt = Stopwatch.GetTimestamp();
        timer.Start();
    }

    private void SetCaptureSource(nint source)
    {
        returnWindow = source;
        captureCardFocus = board is not null && source == board.Handle ? board.GetFocusedCard() : null;
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        if (scheduledSurface || visible is not (Surface.Warning or Surface.Preview))
        {
            return;
        }
        if (EndIfPlacementInvalid())
        {
            return;
        }
        if (!ManualPreviewAllowed(checkPresentation: visible != Surface.Preview || !preview.HasForeground))
        {
            Dismiss();
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
            var task = previewLayout == BreakLayout.Compact && !reminders.Settings.TimerOnly ? GetReturnTask() : null;
            if (!ManualPreviewAllowed(checkPresentation: true) || !warning.PlacementIsCurrent)
            {
                Dismiss();
                return;
            }
            var source = desktop.WorkWindow();
            if (!preview.TryShow(source, previewLayout, task?.Title, task?.NextStep))
            {
                Dismiss();
                return;
            }
            returnWindow = source;
            visible = Surface.Preview;
            desktop.DismissLabel = "End break";
            StartClock();
        }
        else if (visible == Surface.Preview)
        {
            Dismiss();
        }
    }

    private BoardTask? GetReturnTask()
    {
        try
        {
            return tasks.ReturnTask();
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            return null;
        }
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

        var cardFocus = captureCardFocus;
        Dismiss();
        if (board?.AppWindow.IsVisible == true)
        {
            board.RefreshAfterCapture(cardFocus);
        }
        return true;
    }

    private void Dismiss()
    {
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
                preview.StopSceneEntrance();
                SurfaceWindow.HideAndReturn(preview, returnWindow);
                break;
        }
        returnWindow = 0;
        captureCardFocus = null;
    }
}
