using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class ReminderSettingsPage : UserControl
{
    private readonly ToggleSwitch enabled = new() { Header = "Scheduled reminders" };
    private readonly ToggleSwitch hours = new() { Header = "Limit reminders to working hours" };
    private readonly ComboBox breakDisplay = CreateBreakDisplaySelector();
    private readonly NumberBox work = Duration("Work interval (seconds)", 5, 86400);
    private readonly NumberBox shortBreak = Duration("Short break (seconds)", 1, 3600);
    private readonly NumberBox longBreak = Duration("Long break (seconds)", 1, 3600);
    private readonly TimePicker start = new() { Header = "Start" };
    private readonly TimePicker end = new() { Header = "End" };
    private readonly Grid times = new() { ColumnSpacing = 12 };
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly TextBlock status = SurfaceStyle.SecondaryLabel("", 14);
    private readonly Func<ReminderSettings, bool> save;
    private ReminderSettings saved = ReminderSettings.Default;
    private bool loadError;
    private double textScale = 1;

    internal ReminderSettingsPage(Func<ReminderSettings, bool> save)
    {
        this.save = save;
        var title = SurfaceStyle.Label("Breaks", 27, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        var cadence = SurfaceStyle.SecondaryLabel("A long break follows three completed short breaks.", 13);
        cadence.TextWrapping = TextWrapping.Wrap;
        var privacy = SurfaceStyle.SecondaryLabel("Pause for presentation from the tray whenever you share your screen.", 13);
        privacy.TextWrapping = TextWrapping.Wrap;
        status.TextWrapping = TextWrapping.Wrap;
        times.ColumnDefinitions.Add(new ColumnDefinition());
        times.ColumnDefinitions.Add(new ColumnDefinition());
        times.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        times.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        times.Children.Add(start);
        Grid.SetColumn(end, 1);
        times.Children.Add(end);
        times.SizeChanged += (_, _) => LayoutTimes();
        hours.Toggled += (_, _) => times.Visibility = hours.IsOn ? Visibility.Visible : Visibility.Collapsed;

        var saveButton = new Button { Content = "Save settings", HorizontalAlignment = HorizontalAlignment.Right };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
        {
            saveButton.Style = style;
        }
        saveButton.Click += (_, _) => Save();
        var discard = new Button { Content = "Discard changes", HorizontalAlignment = HorizontalAlignment.Right };
        discard.Click += (_, _) => SetForm(saved, loadError);

        var form = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
        form.Children.Add(title);
        form.Children.Add(status);
        form.Children.Add(enabled);
        form.Children.Add(work);
        form.Children.Add(shortBreak);
        form.Children.Add(longBreak);
        form.Children.Add(cadence);
        form.Children.Add(hours);
        form.Children.Add(times);
        form.Children.Add(breakDisplay);
        form.Children.Add(privacy);
        form.Children.Add(error);
        form.Children.Add(saveButton);
        form.Children.Add(discard);
        Content = SurfaceStyle.Scroll(form);
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
            if (element is NumberBox or TimePicker)
            {
                return true;
            }
        }
        return false;
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        if (textScale != state.TextScale)
        {
            textScale = state.TextScale;
            LayoutTimes();
        }
    }

    private void LayoutTimes()
    {
        var stack = times.ActualWidth < 440 * textScale;
        times.RowSpacing = stack ? 8 : 0;
        Grid.SetColumnSpan(start, stack ? 2 : 1);
        Grid.SetColumn(end, stack ? 0 : 1);
        Grid.SetColumnSpan(end, stack ? 2 : 1);
        Grid.SetRow(end, stack ? 1 : 0);
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
        work.Value = settings.WorkSeconds;
        shortBreak.Value = settings.ShortSeconds;
        longBreak.Value = settings.LongSeconds;
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
        if (!Whole(work.Value) || !Whole(shortBreak.Value) || !Whole(longBreak.Value))
        {
            Report("Enter each duration in whole seconds.");
            return;
        }

        var display = (BreakDisplay)breakDisplay.SelectedIndex;
        if (!Enum.IsDefined(display))
        {
            Report("Choose a break display.");
            return;
        }

        var settings = ReadDraft();
        if (settings is null || !settings.IsValid)
        {
            Report("Check the durations and choose different start and end times for working hours.");
        }
        else if (!save(settings))
        {
            Report("Couldn’t save reminder settings. Your changes are still here. Try again.");
        }
        else
        {
            saved = settings;
            loadError = false;
            error.IsOpen = false;
        }
    }

    private ReminderSettings? ReadDraft()
    {
        if (!Whole(work.Value) || !Whole(shortBreak.Value) || !Whole(longBreak.Value))
        {
            return null;
        }
        return new ReminderSettings(enabled.IsOn, (int)work.Value, (int)shortBreak.Value, (int)longBreak.Value,
            hours.IsOn, (int)start.Time.TotalMinutes, (int)end.Time.TotalMinutes, (BreakDisplay)breakDisplay.SelectedIndex);
    }

    private void Report(string message)
    {
        error.Message = message;
        error.IsOpen = true;
    }

    private static bool Whole(double value) => double.IsFinite(value) && value == Math.Truncate(value);

    private static NumberBox Duration(string label, int minimum, int maximum) => new()
    {
        Header = label,
        Minimum = minimum,
        Maximum = maximum,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };
}
