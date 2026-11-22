using Microsoft.UI.Dispatching;

namespace Afterbreak;

internal sealed partial class AfterbreakSession
{
    private sealed class ReminderClock : TimeProvider
    {
        // .NET 10 on Windows includes time spent asleep in TickCount64.
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Environment.TickCount64;
    }

    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);
    private DispatcherQueueTimer reminderTimer = null!;
    private ReminderScheduler reminders = null!;
    private TimeProvider reminderClock = null!;
    private bool reminderStorageError;
    private bool reminderLoadError;
    private bool scheduledSurface;
    private bool checkpointDirty;
    private long lastCheckpoint;
    private ReminderPause systemPauseForFocus;
    private BoardTask? nextFocusTask;
    private bool focusTaskUnavailable;
    private string? reminderActionError;

    private void InitializeReminders(DispatcherQueue dispatcher, TimeProvider clock)
    {
        reminderClock = clock;
        reminders = new ReminderScheduler(clock, ReminderSettings.Default);
        reminderTimer = dispatcher.CreateTimer();
        reminderTimer.Interval = TimeSpan.FromMilliseconds(250);
        reminderTimer.Tick += (_, _) => UpdateScheduledReminders();
        desktop.EligibilityChanged += reason => UpdateScheduledReminders(recheckPresentation: true, reason);
        desktop.DisplaysChanged += () => EndIfPlacementInvalid("display-notification");
        lastCheckpoint = reminderClock.GetTimestamp();
        TryLoadReminderSettings();
    }

    private void TryLoadReminderSettings()
    {
        try
        {
            var (settings, checkpoint) = tasks.LoadReminders();
            var alreadyPaused = reminders.ManuallyPaused;
            reminders = new ReminderScheduler(reminderClock, settings, checkpoint);
            if (alreadyPaused)
            {
                reminders.SetManualPause(true);
            }
            reminderStorageError = reminderLoadError = false;
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            reminderStorageError = reminderLoadError = true;
        }

        UpdateScheduledReminders();
        UpdateReminderTimer();
    }

    private bool ManualPreviewAllowed(bool checkPresentation) => !reminderLoadError && !reminders.ManuallyPaused &&
        desktop.GetSystemPause(checkPresentation) == ReminderPause.None;

    private void UpdateReminderTimer()
    {
        var shouldRun = (reminders.Settings.Enabled && !reminderStorageError) ||
            main is { CurrentPage: ManagementPage.Focus, AppWindow.IsVisible: true };
        if (shouldRun && !reminderTimer.IsRunning)
        {
            reminderTimer.Start();
        }
        else if (!shouldRun && reminderTimer.IsRunning)
        {
            reminderTimer.Stop();
        }
    }

    private bool IsEditingOrPreviewing() => visible == Surface.Capture ||
        (!scheduledSurface && visible is (Surface.Warning or Surface.Preview)) ||
        (desktop.MenuOpen && visible != Surface.Preview) || main?.DefersReminders == true;

    private void UpdateScheduledReminders(bool recheckPresentation = false, string reason = "reminder-tick")
    {
        if (EndIfPlacementInvalid(reason))
        {
            return;
        }

        // The shell treats our own full-screen break as busy on some displays. Check again on
        // explicit system notifications or after the break loses foreground, and before every show.
        var checkPresentation = recheckPresentation || visible != Surface.Preview ||
            (!preview.HasForeground && !desktop.TrayFlyoutHasForeground);
        var systemPause = desktop.GetSystemPause(checkPresentation);
        if (!scheduledSurface && visible is (Surface.Warning or Surface.Preview) &&
            (systemPause != ReminderPause.None || reminders.ManuallyPaused))
        {
            Dismiss($"{reason}-eligibility");
        }

        if (reminders.Settings.Enabled && !reminderStorageError)
        {
            var phase = reminders.Phase;
            var pauses = reminders.PausedBy;
            var wasShowingWarning = scheduledSurface && visible == Surface.Warning;
            var environment = systemPause | (IsEditingOrPreviewing() ? ReminderPause.Interaction : ReminderPause.None);
            reminders.Update(environment);
            if (scheduledSurface && (reminders.PausedBy != ReminderPause.None || phase != reminders.Phase))
            {
                Dismiss($"{reason}-phase-or-pause");
            }
            if (checkpointDirty || phase != reminders.Phase || pauses != reminders.PausedBy ||
                reminderClock.GetElapsedTime(lastCheckpoint) >= CheckpointInterval)
            {
                PersistCheckpoint();
            }
            SynchronizeScheduledSurface(wasShowingWarning);
        }

        UpdateReminderStatus(systemPause);
        UpdateReminderTimer();
    }

    private void SynchronizeScheduledSurface(bool wasShowingWarning)
    {
        var phase = reminders.Phase;
        if (reminderStorageError || reminders.PausedBy != ReminderPause.None || phase is ReminderPhase.Work or ReminderPhase.Snoozed)
        {
            if (scheduledSurface)
            {
                Dismiss();
            }
            return;
        }

        var intended = phase == ReminderPhase.Warning ? Surface.Warning : Surface.Preview;
        if (intended == Surface.Warning)
        {
            warning.SetSnoozeDuration(reminders.Settings.SnoozeSeconds);
        }
        if (!scheduledSurface || visible != intended)
        {
            if (scheduledSurface)
            {
                Dismiss();
            }

            if (intended == Surface.Preview)
            {
                preview.SetPreview(ReadBreakView(), reminders.IsLongBreak);
            }
            var systemPause = desktop.GetSystemPause(checkPresentation: true);
            // The original warning's display can disappear during checkpoint I/O or task lookup.
            if (intended == Surface.Preview && wasShowingWarning && !warning.PlacementIsCurrent)
            {
                SkipForDisplayLoss();
                return;
            }
            var environment = systemPause | (IsEditingOrPreviewing() ? ReminderPause.Interaction : ReminderPause.None);
            reminders.StartVisibleCountdown();
            // Working hours may have ended during persistence or task lookup.
            reminders.Update(environment);
            if (reminders.Phase != phase || reminders.PausedBy != ReminderPause.None)
            {
                checkpointDirty = true;
                return;
            }

            var source = desktop.WorkWindow();
            var shown = intended == Surface.Warning
                ? warning.TryShow(source, reminders.IsLongBreak ? "Long break" : "Short break")
                : preview.TryShow(source);
            if (!shown)
            {
                diagnostics?.Write($"event=not-shown phase={intended} mode=scheduled reason=prepare-surface " +
                    (intended == Surface.Preview ? preview.DescribePlacement() : warning.DescribePlacement()));
                SkipForDisplayLoss();
                return;
            }
            returnWindow = source;
            visible = intended;
            scheduledSurface = true;
            if (intended == Surface.Preview)
            {
                desktop.DismissLabel = "End break";
            }
            reminders.StartVisibleCountdown();
            TraceBreakShown(reminders.Remaining);
            if (intended == Surface.Preview)
            {
                PlayBreakStartCue();
            }
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(reminders.Remaining.TotalSeconds));
        if (intended == Surface.Warning)
        {
            warning.Update(seconds);
        }
        else
        {
            preview.Update(seconds);
        }
    }

    private void SkipForDisplayLoss()
    {
        reminders.Skip();
        PersistCheckpoint();
    }

    private bool EndIfPlacementInvalid(string reason)
    {
        var valid = visible switch
        {
            Surface.Warning => warning.AppWindow.IsVisible && warning.PlacementIsCurrent,
            Surface.Preview => preview.PlacementIsCurrent,
            _ => true,
        };
        if (valid)
        {
            return false;
        }

        TraceBreak("placement-invalid", reason);
        if (scheduledSurface)
        {
            SkipForDisplayLoss();
        }
        Dismiss();
        return true;
    }

    private void StopScheduledSurfaceForInput()
    {
        if (!scheduledSurface)
        {
            return;
        }

        if (visible == Surface.Preview)
        {
            reminders.Skip();
        }
        else
        {
            reminders.Update(ReminderPause.Interaction);
        }
        Dismiss();
        checkpointDirty = true;
    }

    private void EndVisibleBreak()
    {
        if (visible is not (Surface.Warning or Surface.Preview))
        {
            return;
        }

        if (scheduledSurface)
        {
            SkipReminder();
        }
        else
        {
            Dismiss();
        }
    }

    private void SnoozeReminder()
    {
        if (reminders.Phase == ReminderPhase.Warning && !reminderStorageError)
        {
            var candidate = reminders.Copy();
            candidate.Snooze();
            CompleteReminderAction(candidate);
        }
    }

    private void SkipReminder()
    {
        if (reminders.Phase != ReminderPhase.Work && !reminders.WorkPaused && !reminderStorageError)
        {
            var candidate = reminders.Copy();
            candidate.Skip();
            CompleteReminderAction(candidate);
        }
    }

    private string? CompleteReminderAction(ReminderScheduler candidate)
    {
        if (!TrySaveExplicitChange(candidate))
        {
            reminderActionError = "Couldn’t save. Try again.";
            main?.FocusHome.SetActionError(reminderActionError);
            if (scheduledSurface)
            {
                if (visible == Surface.Warning)
                {
                    warning.ShowSaveError(reminderActionError);
                }
                else
                {
                    preview.ShowSaveError(reminderActionError);
                }
            }
            UpdateReminderStatus(systemPauseForFocus);
            return reminderActionError;
        }
        if (scheduledSurface)
        {
            Dismiss();
        }
        reminders.StartVisibleCountdown();
        UpdateScheduledReminders();
        return null;
    }

    private bool SaveSettings(ReminderSettings settings)
    {
        if (reminderLoadError || !settings.IsValid)
        {
            return false;
        }

        var candidate = reminders.WithSettings(settings);
        if (!TrySaveExplicitChange(candidate))
        {
            return false;
        }

        main?.Capture.RebaseBreakDisplay(settings.BreakDisplay);
        reminderLoadError = false;
        UpdateReminderTimer();
        UpdateScheduledReminders();
        return true;
    }

    private bool TrySaveExplicitChange(ReminderScheduler candidate)
    {
        try
        {
            tasks.SaveReminders(candidate.Settings, candidate.Checkpoint());
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            return false;
        }
        reminders = candidate;
        OnCheckpointSaved();
        reminderActionError = null;
        main?.FocusHome.SetActionError(null);
        return true;
    }

    private void TogglePresentationPause()
    {
        var paused = !reminders.ManuallyPaused;
        if (reminderLoadError)
        {
            reminders.SetManualPause(paused);
            UpdateScheduledReminders(recheckPresentation: true, reason: "manual-presentation-toggle");
            return;
        }

        if (paused)
        {
            reminders.SetManualPause(true);
            UpdateScheduledReminders(recheckPresentation: true, reason: "manual-presentation-toggle");
        }

        if (TryPersist(reminders.Settings, reminders.Checkpoint() with { ManuallyPaused = paused }))
        {
            reminders.SetManualPause(paused);
            UpdateReminderTimer();
        }
        UpdateScheduledReminders(recheckPresentation: true, reason: "manual-presentation-toggle");
    }

    private void PersistCheckpoint() => TryPersist(reminders.Settings, reminders.Checkpoint());

    private bool TryPersist(ReminderSettings settings, ReminderCheckpoint checkpoint)
    {
        try
        {
            tasks.SaveReminders(settings, checkpoint);
            OnCheckpointSaved();
            return true;
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            reminderStorageError = true;
            if (scheduledSurface)
            {
                Dismiss();
            }
            UpdateReminderTimer();
            UpdateReminderStatus(systemPauseForFocus);
            return false;
        }
    }

    private void OnCheckpointSaved()
    {
        lastCheckpoint = reminderClock.GetTimestamp();
        checkpointDirty = false;
        reminderStorageError = false;
    }

    private void UpdateReminderStatus(ReminderPause systemPause)
    {
        systemPauseForFocus = systemPause;
        var pauses = reminders.PausedBy;
        var phase = reminders.Phase;
        desktop.ManuallyPaused = reminders.ManuallyPaused;
        desktop.PreviewAvailable = visible == Surface.None && !reminderLoadError && !reminders.ManuallyPaused;
        desktop.SnoozeAvailable = !reminderStorageError && reminders.Settings.Enabled && !reminders.ManuallyPaused &&
            phase == ReminderPhase.Warning && (scheduledSurface || pauses == ReminderPause.Interaction);
        desktop.SnoozeSeconds = reminders.Settings.SnoozeSeconds;
        desktop.SkipAvailable = desktop.SnoozeAvailable ||
            (!reminderStorageError && reminders.Settings.Enabled && !reminders.WorkPaused && phase == ReminderPhase.Snoozed);
        desktop.ReminderStatus = reminderActionError ?? (reminders.ManuallyPaused ? "Paused for presentation" :
            reminderStorageError ? "Reminders unavailable · Check settings" :
            !reminders.Settings.Enabled ? "Reminders off" :
            reminders.WorkPaused ? "Reminders on · Work paused" :
            (pauses & ReminderPause.OutsideHours) != 0 ? "Reminders on · Outside working hours" :
            ((pauses | systemPause) & (ReminderPause.Locked | ReminderPause.Suspended | ReminderPause.Presentation)) != 0 ? "Reminders on · Paused by Windows" :
            phase == ReminderPhase.Snoozed ? "Reminders on · Snoozed" :
            phase is ReminderPhase.Warning or ReminderPhase.Break ? "Reminders on · Break due" : "Reminders on · Working interval");
        main?.Breaks.SetStatus(desktop.ReminderStatus);
        PublishFocusSnapshot();
        desktop.RefreshTrayFlyout();
    }

    private void RefreshFocusTask()
    {
        try
        {
            nextFocusTask = tasks.ReturnTask();
            focusTaskUnavailable = false;
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            nextFocusTask = null;
            focusTaskUnavailable = true;
        }
        PublishFocusSnapshot();
    }

    private void PublishFocusSnapshot()
    {
        if (main is not { CurrentPage: ManagementPage.Focus } host)
        {
            return;
        }

        ReminderPhase? previewPhase = !scheduledSurface ? visible switch
        {
            Surface.Warning => ReminderPhase.Warning,
            Surface.Preview => ReminderPhase.Break,
            _ => null,
        } : null;
        var settings = reminders.Settings;
        var pauses = systemPauseForFocus | reminders.PausedBy |
            (reminders.ManuallyPaused ? ReminderPause.Manual : ReminderPause.None);
        if (settings.Enabled && settings.WindowOn(reminderClock.GetLocalNow()) is null)
        {
            pauses |= ReminderPause.OutsideHours;
        }
        if (settings.Enabled && IsEditingOrPreviewing())
        {
            pauses |= ReminderPause.Interaction;
        }
        host.FocusHome.SetSnapshot(new FocusSnapshot(settings.Enabled, reminders.Phase,
            WholeSeconds(reminders.Remaining), pauses, reminders.CompletedShorts,
            WholeSeconds(settings.BreakLength(reminders.CompletedShorts)), nextFocusTask, focusTaskUnavailable,
            reminderStorageError, previewPhase, previewPhase is null ? 0 : WholeSeconds(ManualPreviewRemaining),
            AvailableFocusActions(systemPauseForFocus), settings.LongBreaks, settings.ShortsBeforeLong, settings.SnoozeSeconds));
    }

    private static int WholeSeconds(TimeSpan remaining) => Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));

    private FocusAction AvailableFocusActions(ReminderPause systemPause)
    {
        var actions = reminderLoadError ? FocusAction.None : reminders.Settings.Enabled ? FocusAction.Stop : FocusAction.Start;
        if (visible == Surface.None && !reminderLoadError && !reminders.ManuallyPaused && systemPause == ReminderPause.None)
        {
            actions |= FocusAction.Preview;
        }
        if (!scheduledSurface && visible is (Surface.Warning or Surface.Preview))
        {
            return actions | FocusAction.CancelPreview;
        }
        if (!reminders.Settings.Enabled || reminderStorageError || visible == Surface.Capture)
        {
            return actions;
        }

        if (reminders.Phase is ReminderPhase.Work or ReminderPhase.Snoozed)
        {
            actions |= reminders.WorkPaused ? FocusAction.Resume : FocusAction.Pause;
            if (!reminders.WorkPaused && reminders.Phase == ReminderPhase.Snoozed)
            {
                actions |= FocusAction.Skip;
            }
        }
        else if (reminders.Phase == ReminderPhase.Warning && (scheduledSurface || reminders.PausedBy == ReminderPause.Interaction))
        {
            actions |= FocusAction.Snooze | FocusAction.Skip;
        }
        else if (reminders.Phase == ReminderPhase.Break && scheduledSurface)
        {
            actions |= FocusAction.EndBreak;
        }
        return actions;
    }

    private string? ExecuteFocusAction(FocusAction action)
    {
        var checkPresentation = visible != Surface.Preview || (!preview.HasForeground && !desktop.TrayFlyoutHasForeground);
        systemPauseForFocus = desktop.GetSystemPause(checkPresentation);
        if ((AvailableFocusActions(systemPauseForFocus) & action) == 0)
        {
            PublishFocusSnapshot();
            return "That action is unavailable right now.";
        }

        if (action is FocusAction.Start or FocusAction.Stop)
        {
            var enabled = action == FocusAction.Start;
            var result = CompleteReminderAction(reminders.WithSettings(reminders.Settings with { Enabled = enabled }));
            if (result is null)
            {
                main?.Breaks.RebaseEnabled(enabled);
            }
            return result;
        }
        if (action == FocusAction.Preview)
        {
            return TryStartManualPreview(main!.Handle, sample: false);
        }
        if (action == FocusAction.CancelPreview)
        {
            Dismiss();
            UpdateScheduledReminders();
            return null;
        }

        var candidate = reminders.Copy();
        switch (action)
        {
            case FocusAction.Pause:
            case FocusAction.Resume:
                var environment = systemPauseForFocus | (IsEditingOrPreviewing() ? ReminderPause.Interaction : ReminderPause.None);
                if (!candidate.SetWorkPause(action == FocusAction.Pause, environment))
                {
                    UpdateScheduledReminders();
                    return "The work interval has ended. Use the available break controls.";
                }
                break;
            case FocusAction.Snooze:
                candidate.Snooze();
                break;
            case FocusAction.Skip:
            case FocusAction.EndBreak:
                candidate.Skip();
                break;
            default:
                return "That action is unavailable right now.";
        }
        return CompleteReminderAction(candidate);
    }
}
