using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace OpenBreak;

internal sealed class ReminderSettingsPage : UserControl
{
    private readonly ToggleSwitch enabled = new();
    private readonly ToggleSwitch hours = new();
    private readonly ToggleSwitch longBreaks = new();
    private readonly ToggleSwitch finishTyping = new();
    private readonly ComboBox breakDisplay = CreateBreakDisplaySelector();
    private readonly DurationEditor work = new("Work interval", "BreaksWork", 1440);
    private readonly DurationEditor shortBreak = new("Short break", "BreaksShort", 60);
    private readonly DurationEditor longBreak = new("Long break", "BreaksLong", 60);
    private readonly DurationEditor snooze = new("Snooze", "BreaksSnooze", 60);
    private readonly TextBox cadence = NumberField();
    private readonly TimePicker start = new();
    private readonly TimePicker end = new();
    private readonly SettingsLayout layout = new("Breaks");
    private readonly StackPanel times = new() { Spacing = SurfaceStyle.SectionGap };
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly TextBlock status = ManagementStyle.BodyText("");
    private readonly Func<ReminderSettings, bool> save;
    private ReminderSettings saved = ReminderSettings.Default;
    private bool loadError;

    internal ReminderSettingsPage(Func<ReminderSettings, bool> save)
    {
        this.save = save;
        status.Visibility = Visibility.Collapsed;
        var reminders = layout.Row("Scheduled reminders", enabled, "BreaksEnabled");
        var schedule = SettingsLayout.Group("Work and rest", status, reminders,
            layout.Row("Work interval", work, "BreaksWork"),
            layout.Row("Short break", shortBreak, "BreaksShort"),
            layout.Row("Long breaks", longBreaks, "BreaksLongEnabled",
                "Only completed short breaks count. Turning long breaks off clears the count."),
            layout.Row("Long break", longBreak, "BreaksLong"),
            layout.Row("Short breaks before a long break (1–12)", cadence, "BreaksCadence"));

        times.Children.Add(layout.Row("Start", start, "BreaksHoursStart"));
        times.Children.Add(layout.Row("End", end, "BreaksHoursEnd"));
        times.Visibility = Visibility.Collapsed;
        hours.Toggled += (_, _) => times.Visibility = hours.IsOn ? Visibility.Visible : Visibility.Collapsed;
        var workingHours = SettingsLayout.Group("Working hours",
            layout.Row("Limit reminders to working hours", hours, "BreaksHours"), times);
        var warnings = SettingsLayout.Group("Break warnings and display",
            layout.Row("Break display", breakDisplay, "BreaksDisplay"),
            layout.Row("Snooze", snooze, "BreaksSnooze"),
            layout.Row("Finish typing before a break", finishTyping, "BreaksFinishTyping",
                "Hold the final warning second until 3 seconds without keyboard or mouse input."),
            ManagementStyle.BodyText("Pause for presentation from the tray whenever you share your screen."));

        layout.Form.Children.Add(ManagementStyle.Card(schedule));
        layout.Form.Children.Add(ManagementStyle.Card(workingHours));
        layout.Form.Children.Add(ManagementStyle.Card(warnings));
        var saveButton = ManagementStyle.Button("Save settings", "BreaksSave", Save, primary: true);
        var discard = ManagementStyle.Button("Discard changes", "BreaksDiscard", () => SetForm(saved, loadError));
        layout.SetFooter(SettingsLayout.Footer(error, saveButton, discard));
        Content = layout;
    }

    internal static ComboBox CreateBreakDisplaySelector()
    {
        var selector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        selector.Items.Add("Saved task board (default)");
        selector.Items.Add("One task");
        selector.Items.Add("Timer only");
        return selector;
    }

    internal bool IsDirty => ReadDraft() != saved;
    internal bool HasFocusedEditor => breakDisplay.IsDropDownOpen || HasTimingEditorFocus();

    private bool HasTimingEditorFocus()
    {
        if (XamlRoot is not { } xamlRoot)
        {
            return false;
        }
        var focused = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
        for (var element = focused; element is not null && element != this; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or TimePicker)
            {
                return true;
            }
        }
        return false;
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        work.SetTextScale(state.TextScale);
        shortBreak.SetTextScale(state.TextScale);
        longBreak.SetTextScale(state.TextScale);
        snooze.SetTextScale(state.TextScale);
        layout.ApplyAppearance(state);
    }

    private static Grid PairGrid()
    {
        var pair = new Grid { ColumnSpacing = SurfaceStyle.ContentGap };
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return pair;
    }

    private static void LayoutPair(Grid pair, FrameworkElement first, FrameworkElement second, double minimumWidth, double scale)
    {
        var stack = pair.ActualWidth < minimumWidth * scale;
        pair.RowSpacing = stack ? SurfaceStyle.ControlGap : 0;
        Grid.SetColumnSpan(first, stack ? 2 : 1);
        Grid.SetColumn(second, stack ? 0 : 1);
        Grid.SetColumnSpan(second, stack ? 2 : 1);
        Grid.SetRow(second, stack ? 1 : 0);
    }

    internal void SetForm(ReminderSettings settings, bool storageError)
    {
        saved = settings;
        loadError = storageError;
        enabled.IsOn = settings.Enabled;
        hours.IsOn = settings.WorkingHours;
        start.Time = TimeSpan.FromMinutes(settings.StartMinute);
        end.Time = TimeSpan.FromMinutes(settings.EndMinute);
        times.Visibility = settings.WorkingHours ? Visibility.Visible : Visibility.Collapsed;
        work.SetSeconds(settings.WorkSeconds);
        shortBreak.SetSeconds(settings.ShortSeconds);
        longBreak.SetSeconds(settings.LongSeconds);
        snooze.SetSeconds(settings.SnoozeSeconds);
        longBreaks.IsOn = settings.LongBreaks;
        finishTyping.IsOn = settings.FinishTyping;
        cadence.Text = settings.ShortsBeforeLong.ToString();
        breakDisplay.SelectedIndex = (int)settings.BreakDisplay;
        error.Severity = InfoBarSeverity.Error;
        error.Message = "Reminder settings are unavailable. Return to Breaks to retry loading your settings.";
        error.IsOpen = storageError;
    }

    internal void RebaseBreakDisplay(BreakDisplay display)
    {
        if (breakDisplay.SelectedIndex == (int)saved.BreakDisplay)
        {
            breakDisplay.SelectedIndex = (int)display;
        }
        saved = saved with { BreakDisplay = display };
    }

    internal void RebaseEnabled(bool isEnabled)
    {
        if (enabled.IsOn == saved.Enabled)
        {
            enabled.IsOn = isEnabled;
        }
        saved = saved with { Enabled = isEnabled };
    }

    internal void SetStatus(string description)
    {
        if (status.Text != description)
        {
            status.Text = description;
            status.Visibility = description.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void Save()
    {
        var display = (BreakDisplay)breakDisplay.SelectedIndex;
        if (!Enum.IsDefined(display))
        {
            Report("Choose a break display.");
            return;
        }

        var settings = ReadDraft();
        if (settings is null)
        {
            Report("Enter whole minutes and seconds, with seconds from 0 to 59. Choose 1 to 12 short breaks.");
        }
        else if (settings.WorkingHours && settings.StartMinute == settings.EndMinute)
        {
            Report("Choose different start and end times for working hours.");
        }
        else if (!settings.IsValid)
        {
            Report("Set work to 5 seconds–24 hours and breaks and snooze to 1 second–60 minutes.");
        }
        else if (!save(settings))
        {
            Report("Couldn’t save reminder settings. Your changes are still here. Try again.");
        }
        else
        {
            saved = settings;
            loadError = false;
            error.Severity = InfoBarSeverity.Success;
            error.Message = "Break settings saved.";
            error.IsOpen = true;
        }
    }

    private ReminderSettings? ReadDraft()
    {
        if (work.ReadSeconds() is not int workSeconds || shortBreak.ReadSeconds() is not int shortSeconds ||
            longBreak.ReadSeconds() is not int longSeconds || snooze.ReadSeconds() is not int snoozeSeconds ||
            !int.TryParse(cadence.Text, out var shortsBeforeLong) || shortsBeforeLong is < 1 or > ReminderSettings.MaxShortsBeforeLong)
        {
            return null;
        }
        return new ReminderSettings(enabled.IsOn, workSeconds, shortSeconds, longSeconds,
            hours.IsOn, (int)start.Time.TotalMinutes, (int)end.Time.TotalMinutes, (BreakDisplay)breakDisplay.SelectedIndex)
        {
            LongBreaks = longBreaks.IsOn,
            ShortsBeforeLong = shortsBeforeLong,
            SnoozeSeconds = snoozeSeconds,
            FinishTyping = finishTyping.IsOn,
        };
    }

    private void Report(string message)
    {
        error.Severity = InfoBarSeverity.Error;
        error.Message = message;
        error.IsOpen = true;
    }

    private static TextBox NumberField() => new()
    {
        InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
    };

    private sealed class DurationEditor : Grid
    {
        private readonly TextBox minutes;
        private readonly TextBox seconds;
        private readonly int maximumMinutes;
        private double textScale = 1;

        internal DurationEditor(string label, string id, int maximumMinutes)
        {
            this.maximumMinutes = maximumMinutes;
            minutes = Field("Minutes");
            seconds = Field("Seconds");
            ColumnSpacing = SurfaceStyle.ControlGap;
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition());
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Children.Add(minutes);
            Children.Add(seconds);
            SizeChanged += (_, _) => Layout();
            Layout();

            TextBox Field(string unit)
            {
                var field = NumberField();
                field.Header = ManagementStyle.BodyText(unit);
                AutomationProperties.SetName(field, $"{label}, {unit.ToLowerInvariant()}");
                AutomationProperties.SetAutomationId(field, id + unit);
                return field;
            }
        }

        internal void SetSeconds(int value)
        {
            minutes.Text = (value / ReminderDuration.SecondsPerMinute).ToString();
            seconds.Text = (value % ReminderDuration.SecondsPerMinute).ToString();
        }

        internal int? ReadSeconds() => int.TryParse(minutes.Text, out var wholeMinutes) && wholeMinutes >= 0 &&
            wholeMinutes <= maximumMinutes && int.TryParse(seconds.Text, out var wholeSeconds) &&
            wholeSeconds is >= 0 and < ReminderDuration.SecondsPerMinute
            ? wholeMinutes * ReminderDuration.SecondsPerMinute + wholeSeconds : null;

        internal void SetTextScale(double scale)
        {
            textScale = scale;
            Layout();
        }

        private void Layout() => LayoutPair(this, minutes, seconds, 230, textScale);
    }
}
