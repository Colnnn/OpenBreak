using Microsoft.UI.Dispatching;

namespace Afterbreak;

internal sealed partial class AfterbreakSession
{
    private sealed class ReminderClock : TimeProvider
    {
        // On .NET 8 for Windows this monotonic counter includes time spent asleep.
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Environment.TickCount64;
    }

    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);
    private DispatcherQueueTimer reminderTimer = null!;
    private ReminderScheduler reminders = null!;
    private TimeProvider reminderClock = null!;
    private SettingsWindow? reminderSettings;
    private bool reminderStorageError;
    private bool reminderLoadError;
    private bool scheduledSurface;
    private bool checkpointDirty;
    private long lastCheckpoint;

    private void InitializeReminders(DispatcherQueue dispatcher, TimeProvider clock)
    {
        reminderClock = clock;
        reminders = new ReminderScheduler(clock, ReminderSettings.Default);
        reminderTimer = dispatcher.CreateTimer();
        reminderTimer.Interval = TimeSpan.FromMilliseconds(250);
        reminderTimer.Tick += (_, _) => UpdateScheduledReminders();
        desktop.EligibilityChanged += () => UpdateScheduledReminders(recheckPresentation: true);
        desktop.DisplaysChanged += () => EndIfPlacementInvalid();
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
        if (reminders.Settings.Enabled && !reminderStorageError)
        {
            reminderTimer.Start();
        }
        else
        {
            reminderTimer.Stop();
        }
    }

    private bool IsEditingOrPreviewing() => visible == Surface.Capture ||
        (!scheduledSurface && visible is (Surface.Warning or Surface.Preview)) ||
        (desktop.MenuOpen && visible != Surface.Preview) || reminderSettings?.AppWindow.IsVisible == true ||
        captureSettings?.AppWindow.IsVisible == true || appearanceSettings?.AppWindow.IsVisible == true || board?.IsEditing == true;

    private void UpdateScheduledReminders(bool recheckPresentation = false)
    {
        if (EndIfPlacementInvalid())
        {
            return;
        }

        // The shell treats our own full-screen break as busy on some displays. Check again on
        // explicit system notifications or after the break loses foreground, and before every show.
        var checkPresentation = recheckPresentation || visible != Surface.Preview || !preview.HasForeground;
        var systemPause = desktop.GetSystemPause(checkPresentation);
        if (!scheduledSurface && visible is (Surface.Warning or Surface.Preview) &&
            (systemPause != ReminderPause.None || reminders.ManuallyPaused))
        {
            Dismiss();
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
                Dismiss();
            }
            if (checkpointDirty || phase != reminders.Phase || pauses != reminders.PausedBy ||
                reminderClock.GetElapsedTime(lastCheckpoint) >= CheckpointInterval)
            {
                PersistCheckpoint();
            }
            SynchronizeScheduledSurface(wasShowingWarning);
        }

        UpdateReminderStatus(systemPause);
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
        if (!scheduledSurface || visible != intended)
        {
            if (scheduledSurface)
            {
                Dismiss();
            }

            var task = intended == Surface.Preview && !reminders.Settings.TimerOnly ? GetReturnTask() : null;
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
                : preview.TryShow(source, BreakLayout.Compact, task?.Title, task?.NextStep, reminders.IsLongBreak);
            if (!shown)
            {
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

    private bool EndIfPlacementInvalid()
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

        EndVisibleBreak();
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
            reminders.Snooze();
            FinishReminderAction();
        }
    }

    private void SkipReminder()
    {
        if (reminders.Phase != ReminderPhase.Work && !reminderStorageError)
        {
            reminders.Skip();
            FinishReminderAction();
        }
    }

    private void FinishReminderAction()
    {
        if (scheduledSurface)
        {
            Dismiss();
        }
        PersistCheckpoint();
        UpdateScheduledReminders();
    }

    private bool SaveSettings(ReminderSettings settings)
    {
        var candidate = reminders.WithSettings(settings);
        if (!TryPersist(settings, candidate.Checkpoint()))
        {
            return false;
        }

        reminders = candidate;
        reminderLoadError = false;
        UpdateReminderTimer();
        UpdateScheduledReminders();
        return true;
    }

    private void TogglePresentationPause()
    {
        var paused = !reminders.ManuallyPaused;
        if (reminderLoadError)
        {
            reminders.SetManualPause(paused);
            UpdateScheduledReminders(recheckPresentation: true);
            return;
        }

        if (paused)
        {
            reminders.SetManualPause(true);
            UpdateScheduledReminders(recheckPresentation: true);
        }

        if (TryPersist(reminders.Settings, reminders.Checkpoint() with { ManuallyPaused = paused }))
        {
            reminders.SetManualPause(paused);
            UpdateReminderTimer();
        }
        UpdateScheduledReminders(recheckPresentation: true);
    }

    private void PersistCheckpoint() => TryPersist(reminders.Settings, reminders.Checkpoint());

    private bool TryPersist(ReminderSettings settings, ReminderCheckpoint checkpoint)
    {
        try
        {
            tasks.SaveReminders(settings, checkpoint);
            lastCheckpoint = reminderClock.GetTimestamp();
            checkpointDirty = false;
            reminderStorageError = false;
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
            UpdateReminderStatus(ReminderPause.None);
            return false;
        }
    }

    private void UpdateReminderStatus(ReminderPause systemPause)
    {
        var pauses = reminders.PausedBy;
        var phase = reminders.Phase;
        desktop.ManuallyPaused = reminders.ManuallyPaused;
        desktop.PreviewAvailable = visible == Surface.None && !reminderLoadError && !reminders.ManuallyPaused;
        desktop.SnoozeAvailable = !reminderStorageError && reminders.Settings.Enabled && !reminders.ManuallyPaused &&
            phase == ReminderPhase.Warning && (scheduledSurface || pauses == ReminderPause.Interaction);
        desktop.SkipAvailable = desktop.SnoozeAvailable ||
            (!reminderStorageError && reminders.Settings.Enabled && phase == ReminderPhase.Snoozed);
        desktop.ReminderStatus = reminders.ManuallyPaused ? "Paused for presentation" :
            reminderStorageError ? "Reminders unavailable · Check settings" :
            !reminders.Settings.Enabled ? "Reminders off" :
            (pauses & ReminderPause.OutsideHours) != 0 ? "Reminders on · Outside working hours" :
            ((pauses | systemPause) & (ReminderPause.Locked | ReminderPause.Suspended | ReminderPause.Presentation)) != 0 ? "Reminders on · Paused by Windows" :
            phase == ReminderPhase.Snoozed ? "Reminders on · Snoozed" :
            phase is ReminderPhase.Warning or ReminderPhase.Break ? "Reminders on · Break due" : "Reminders on · Working interval";
        reminderSettings?.SetStatus(desktop.ReminderStatus);
    }
}
