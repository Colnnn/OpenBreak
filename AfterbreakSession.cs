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
    private bool samplePreview;
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

    private readonly record struct CaptureBookmark(BoardWindow.CardFocus? Card);

    private Action<TrayCommand> BindTrayCommand(nint source)
    {
        var bookmark = new CaptureBookmark(FocusedBoardCard(source));
        return command => Execute(command, source, bookmark);
    }

    private void Execute(TrayCommand command, nint source, CaptureBookmark? bookmark = null)
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
                DismissBreakForSettings(preserveSchedule: true);
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
                desktop.DismissTray();
                if (visible == Surface.Capture)
                {
                    var previousSource = returnWindow;
                    var previousCardFocus = captureCardFocus;
                    if (source != 0 && source != returnWindow)
                    {
                        SetCaptureSource(source, bookmark);
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
                samplePreview = command == TrayCommand.PreviewSampleBoard;
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

    private BoardWindow.CardFocus? FocusedBoardCard(nint source) =>
        board is not null && source == board.Handle ? board.GetFocusedCard() : null;

    private void SetCaptureSource(nint source, CaptureBookmark? bookmark)
    {
        returnWindow = source;
        captureCardFocus = bookmark is { } captured ? captured.Card : FocusedBoardCard(source);
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
        if (!ManualPreviewAllowed(checkPresentation: visible != Surface.Preview ||
            (!preview.HasForeground && !desktop.TrayFlyoutHasForeground)))
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
            preview.SetPreview(ReadBreakView(samplePreview));
            if (!ManualPreviewAllowed(checkPresentation: true) || !warning.PlacementIsCurrent)
            {
                Dismiss();
                return;
            }
            var source = desktop.WorkWindow();
            if (!preview.TryShow(source))
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
        desktop.RefreshTrayFlyout();
    }
}
