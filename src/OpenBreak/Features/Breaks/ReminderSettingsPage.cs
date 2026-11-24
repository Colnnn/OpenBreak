using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace OpenBreak;

internal sealed class ReminderSettingsPage : UserControl
{
    private readonly ToggleSwitch enabled = new() { Header = "Scheduled reminders" };
    private readonly ToggleSwitch hours = new() { Header = "Limit reminders to working hours" };
    private readonly ToggleSwitch longBreaks = new() { Header = "Long breaks" };
    private readonly ToggleSwitch finishTyping = new() { Header = "Finish typing before a break" };
    private readonly ComboBox breakDisplay = CreateBreakDisplaySelector();
    private readonly DurationEditor work = new("Work interval", 1440);
    private readonly DurationEditor shortBreak = new("Short break", 60);
    private readonly DurationEditor longBreak = new("Long break", 60);
    private readonly DurationEditor snooze = new("Snooze", 60);
    private readonly TextBox cadence = NumberField("Short breaks before a long break (1–12)", "Completed short breaks before a long break, 1 to 12");
    private readonly TimePicker start = new() { Header = "Start" };
    private readonly TimePicker end = new() { Header = "End" };
    private readonly Grid firstDurations = PairGrid();
    private readonly Grid secondDurations = PairGrid();
    private readonly Grid times = PairGrid();
    private readonly StackPanel actions = new() { Spacing = SurfaceStyle.ControlGap, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly TextBlock status = SurfaceStyle.SecondaryLabel("", SurfaceStyle.Body);
    private readonly Func<ReminderSettings, bool> save;
    private ReminderSettings saved = ReminderSettings.Default;
    private bool loadError;
    private double textScale = 1;

    internal ReminderSettingsPage(Func<ReminderSettings, bool> save)
    {
        this.save = save;
        var title = SurfaceStyle.PageTitle("Breaks");
        var rhythmHint = SurfaceStyle.SecondaryLabel("Only completed short breaks count. Turning long breaks off clears the count.", SurfaceStyle.Body);
        rhythmHint.TextWrapping = TextWrapping.Wrap;
        var privacy = SurfaceStyle.SecondaryLabel("Pause for presentation from the tray whenever you share your screen.", SurfaceStyle.Body);
        privacy.TextWrapping = TextWrapping.Wrap;
        var inputHint = SurfaceStyle.SecondaryLabel(
            "Hold the final warning second until 3 seconds without keyboard or mouse input.", SurfaceStyle.Body);
        inputHint.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHelpText(finishTyping, inputHint.Text);
        status.TextWrapping = TextWrapping.Wrap;
        AddPair(firstDurations, work, shortBreak, 560);
        AddPair(secondDurations, longBreak, cadence, 560);
        AddPair(times, start, end, 440);
        cadence.VerticalAlignment = VerticalAlignment.Bottom;
        hours.Toggled += (_, _) => times.Visibility = hours.IsOn ? Visibility.Visible : Visibility.Collapsed;

        var saveButton = new Button { Content = "Save settings", HorizontalAlignment = HorizontalAlignment.Right };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
        {
            saveButton.Style = style;
        }
        saveButton.Click += (_, _) => Save();
        var discard = new Button { Content = "Discard changes", HorizontalAlignment = HorizontalAlignment.Right };
        discard.Click += (_, _) => SetForm(saved, loadError);

        var form = new StackPanel { Spacing = SurfaceStyle.SectionGap, Margin = new Thickness(SurfaceStyle.PageInset) };
        form.Children.Add(title);
        form.Children.Add(status);
        form.Children.Add(enabled);
        form.Children.Add(Heading("Work and rest", SurfaceStyle.SectionTitle));
        form.Children.Add(firstDurations);
        form.Children.Add(longBreaks);
        form.Children.Add(secondDurations);
        form.Children.Add(rhythmHint);
        form.Children.Add(hours);
        form.Children.Add(times);
        form.Children.Add(Heading("Break warnings and display", SurfaceStyle.SectionTitle));
        form.Children.Add(breakDisplay);
        form.Children.Add(snooze);
        form.Children.Add(finishTyping);
        form.Children.Add(inputHint);
        form.Children.Add(privacy);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(SurfaceStyle.Scroll(form));
        var footer = new StackPanel { Spacing = SurfaceStyle.ControlGap,
            Margin = new Thickness(SurfaceStyle.PageInset, SurfaceStyle.ControlGap, SurfaceStyle.PageInset, SurfaceStyle.ContentGap) };
        actions.Children.Add(saveButton);
        actions.Children.Add(discard);
        footer.Children.Add(error);
        footer.Children.Add(actions);
        footer.SizeChanged += (_, _) => LayoutActions(footer.ActualWidth);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;

        void AddPair(Grid grid, FrameworkElement first, FrameworkElement second, double minimumWidth)
        {
            grid.Children.Add(first);
            grid.Children.Add(second);
            grid.SizeChanged += (_, _) => LayoutPair(grid, first, second, minimumWidth, textScale);
            LayoutPair(grid, first, second, minimumWidth, textScale);
        }
    }

    internal static ComboBox CreateBreakDisplaySelector()
    {
        var selector = new ComboBox { Header = "Break display", HorizontalAlignment = HorizontalAlignment.Stretch };
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
        textScale = state.TextScale;
        work.SetTextScale(textScale);
        shortBreak.SetTextScale(textScale);
        longBreak.SetTextScale(textScale);
        snooze.SetTextScale(textScale);
        LayoutPair(firstDurations, work, shortBreak, 560, textScale);
        LayoutPair(secondDurations, longBreak, cadence, 560, textScale);
        LayoutPair(times, start, end, 440, textScale);
        LayoutActions(ActualWidth - 40);
    }

    private void LayoutActions(double width) => actions.Orientation = width < 300 * textScale
        ? Orientation.Vertical : Orientation.Horizontal;

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

    private static TextBlock Heading(string text, double size, AutomationHeadingLevel level = AutomationHeadingLevel.Level2)
    {
        var heading = SurfaceStyle.Label(text, size, FontWeights.SemiBold);
        heading.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(heading, level);
        return heading;
    }

    private static TextBox NumberField(string header, string name)
    {
        var field = new TextBox
        {
            Header = SurfaceStyle.Label(header, SurfaceStyle.Body),
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
        };
        ((TextBlock)field.Header).TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetName(field, name);
        return field;
    }

    private sealed class DurationEditor : StackPanel
    {
        private readonly Grid fields = PairGrid();
        private readonly TextBox minutes;
        private readonly TextBox seconds;
        private readonly int maximumMinutes;
        private double textScale = 1;

        internal DurationEditor(string label, int maximumMinutes)
        {
            this.maximumMinutes = maximumMinutes;
            minutes = NumberField("Minutes", $"{label}, minutes");
            seconds = NumberField("Seconds", $"{label}, seconds");
            Spacing = SurfaceStyle.TightGap;
            Children.Add(Heading(label, SurfaceStyle.StrongBody, AutomationHeadingLevel.Level3));
            fields.Children.Add(minutes);
            fields.Children.Add(seconds);
            fields.SizeChanged += (_, _) => Layout();
            Children.Add(fields);
            Layout();
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

        private void Layout() => LayoutPair(fields, minutes, seconds, 230, textScale);
    }
}
