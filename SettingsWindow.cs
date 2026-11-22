using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class SettingsWindow : Window
{
    private readonly ToggleSwitch enabled = new() { Header = "Scheduled reminders" };
    private readonly ToggleSwitch hours = new() { Header = "Limit reminders to working hours" };
    private readonly ComboBox breakDisplay = new() { Header = "Break display", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox work = Duration("Work interval (seconds)", 5, 86400);
    private readonly NumberBox shortBreak = Duration("Short break (seconds)", 1, 3600);
    private readonly NumberBox longBreak = Duration("Long break (seconds)", 1, 3600);
    private readonly TimePicker start = new() { Header = "Start" };
    private readonly TimePicker end = new() { Header = "End" };
    private readonly Grid times = new() { ColumnSpacing = 12 };
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly TextBlock status = SurfaceStyle.SecondaryLabel("", 14);
    private readonly Func<ReminderSettings, bool> save;
    private bool placed;
    private bool quitting;
    private double textScale = 1;

    internal SettingsWindow(Func<ReminderSettings, bool> save)
    {
        this.save = save;
        Title = "Afterbreak · Reminder settings";
        breakDisplay.Items.Add("Saved task board (default)");
        breakDisplay.Items.Add("One task");
        breakDisplay.Items.Add("Timer only");

        var title = SurfaceStyle.Label("Reminder settings", 27, FontWeights.SemiBold);
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
        Content = SurfaceStyle.Scroll(form);
        AppWindow.Closing += (_, args) =>
        {
            if (!quitting)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

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

    internal void Show(nint source, ReminderSettings settings, string description, bool storageError)
    {
        if (!AppWindow.IsVisible)
        {
            SetForm(settings, storageError);
        }

        SetStatus(description);
        if (!SurfaceWindow.TryPlaceOrRecover(this, source, 530, 720, placed))
        {
            return;
        }
        placed = true;
        Activate();
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }
    }

    internal void SetForm(ReminderSettings settings, bool storageError)
    {
        enabled.IsOn = settings.Enabled;
        hours.IsOn = settings.WorkingHours;
        start.Time = TimeSpan.FromMinutes(settings.StartMinute);
        end.Time = TimeSpan.FromMinutes(settings.EndMinute);
        times.Visibility = settings.WorkingHours ? Visibility.Visible : Visibility.Collapsed;
        work.Value = settings.WorkSeconds;
        shortBreak.Value = settings.ShortSeconds;
        longBreak.Value = settings.LongSeconds;
        breakDisplay.SelectedIndex = (int)settings.BreakDisplay;
        error.Message = "Reminder settings are unavailable. Reopen this window to retry loading your settings.";
        error.IsOpen = storageError;
    }

    internal void SetStatus(string description)
    {
        if (status.Text != description)
        {
            status.Text = description;
        }
    }

    internal void CloseForQuit()
    {
        quitting = true;
        Close();
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

        var settings = new ReminderSettings(enabled.IsOn, (int)work.Value, (int)shortBreak.Value, (int)longBreak.Value,
            hours.IsOn, (int)start.Time.TotalMinutes, (int)end.Time.TotalMinutes, display);
        if (!settings.IsValid)
        {
            Report("Check the durations and choose different start and end times for working hours.");
        }
        else if (!save(settings))
        {
            Report("Couldn’t save reminder settings. Your changes are still here. Try again.");
        }
        else
        {
            AppWindow.Hide();
        }
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
