using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace OpenBreak;

internal sealed class AppearanceSettingsPage : UserControl
{
    private readonly ComboBox theme = new() { Header = "App theme", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox scene = new() { Header = "Break scenery", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox motion = new() { Header = "Break scene motion", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch sound = new();
    private readonly ToggleSwitch trayCountdown = new();
    private readonly ToggleSwitch floatingTimer = new();
    private readonly ComboBox floatingStyle = new() { Header = "Floating timer style", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch floatingOnTop = new();
    private readonly ScenePreview preview = new();
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Button saveButton = new() { Content = "Save appearance", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel actions = new() { Spacing = SurfaceStyle.ControlGap, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel footer = new()
    {
        Spacing = SurfaceStyle.ControlGap,
        Margin = new Thickness(SurfaceStyle.PageInset, SurfaceStyle.ControlGap, SurfaceStyle.PageInset, SurfaceStyle.ContentGap),
    };
    private AppearancePreferences saved = AppearancePreferences.Default;
    private AppearanceState systemAppearance = AppearancePolicy.Resolve(AppearancePreferences.Default,
        new AppearanceSystem(false, false, false, 1, 0xFFFFFF, 0));
    private bool loadFailed;

    internal AppearanceSettingsPage(Func<AppearancePreferences, bool> save)
    {
        foreach (var label in Enum.GetNames<AppearanceTheme>())
        {
            theme.Items.Add(label);
        }
        foreach (var label in Enum.GetNames<BreakScene>())
        {
            scene.Items.Add(label);
        }
        motion.Items.Add("Follow Windows");
        motion.Items.Add("Off");
        foreach (var label in Enum.GetNames<FloatingTimerStyle>())
        {
            floatingStyle.Items.Add(label);
        }
        scene.SelectionChanged += (_, _) => UpdatePreview();
        LabelToggle(sound, "Play a sound when a break starts");
        LabelToggle(trayCountdown, "Show countdown in the system tray");
        LabelToggle(floatingTimer, "Show a floating desktop timer");
        LabelToggle(floatingOnTop, "Keep the floating timer on top");
        floatingTimer.Toggled += (_, _) => UpdateFloatingControls();

        var sampleTitle = SurfaceStyle.Label("Scene preview", SurfaceStyle.SectionTitle, FontWeights.SemiBold);
        sampleTitle.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(sampleTitle, AutomationHeadingLevel.Level2);
        var sample = new StackPanel { Spacing = SurfaceStyle.ControlGap };
        sample.Children.Add(sampleTitle);
        sample.Children.Add(Note("Sample timer and task. Windows contrast themes hide scenery."));
        sample.Children.Add(preview);

        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
        {
            saveButton.Style = style;
        }
        saveButton.Click += (_, _) =>
        {
            var selected = Draft;
            if (selected.IsValid && save(selected))
            {
                SetPreferences(selected);
            }
            else
            {
                error.Message = "Couldn’t save appearance. Your selections are still here. Try again.";
                error.IsOpen = true;
            }
        };
        var discard = new Button { Content = "Discard changes", HorizontalAlignment = HorizontalAlignment.Right };
        discard.Click += (_, _) => SetPreferences(saved, loadFailed);

        var form = new StackPanel { Spacing = SurfaceStyle.ContentGap, Margin = new Thickness(SurfaceStyle.PageInset) };
        form.Children.Add(SurfaceStyle.PageTitle("Appearance"));
        form.Children.Add(theme);
        form.Children.Add(scene);
        form.Children.Add(sample);
        form.Children.Add(motion);
        form.Children.Add(sound);
        form.Children.Add(Note("Plays a short chime for scheduled breaks and break previews when enabled."));
        var countdown = SurfaceStyle.Label("Countdown visibility", SurfaceStyle.SectionTitle, FontWeights.SemiBold);
        countdown.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(countdown, AutomationHeadingLevel.Level2);
        form.Children.Add(countdown);
        form.Children.Add(trayCountdown);
        form.Children.Add(floatingTimer);
        form.Children.Add(floatingStyle);
        form.Children.Add(floatingOnTop);
        actions.Children.Add(saveButton);
        actions.Children.Add(discard);
        footer.Children.Add(error);
        footer.Children.Add(actions);
        footer.SizeChanged += (_, _) => LayoutActions();
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 760 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(SurfaceStyle.Scroll(form));
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;
        SetPreferences(saved);
    }

    private AppearancePreferences Draft => new((AppearanceTheme)theme.SelectedIndex, (BreakScene)scene.SelectedIndex,
        (SceneMotion)motion.SelectedIndex, sound.IsOn, trayCountdown.IsOn, floatingTimer.IsOn, floatingOnTop.IsOn,
        (FloatingTimerStyle)floatingStyle.SelectedIndex);
    internal bool IsDirty => Draft != saved;
    internal bool HasFocusedEditor => theme.IsDropDownOpen || scene.IsDropDownOpen || motion.IsDropDownOpen ||
        floatingStyle.IsDropDownOpen;

    internal void ApplyAppearance(AppearanceState state)
    {
        systemAppearance = state;
        UpdatePreview();
        LayoutActions();
    }

    private void LayoutActions() =>
        actions.Orientation = footer.ActualWidth < 355 * systemAppearance.TextScale
            ? Orientation.Vertical : Orientation.Horizontal;

    private void UpdatePreview()
    {
        var choice = (BreakScene)scene.SelectedIndex;
        preview.ApplyAppearance(systemAppearance with { Scene = Enum.IsDefined(choice) ? choice : saved.Scene });
    }

    internal void SetPreferences(AppearancePreferences preferences, bool loadFailed = false)
    {
        saved = preferences;
        this.loadFailed = loadFailed;
        theme.SelectedIndex = (int)preferences.Theme;
        scene.SelectedIndex = (int)preferences.Scene;
        motion.SelectedIndex = (int)preferences.Motion;
        sound.IsOn = preferences.BreakStartSound;
        trayCountdown.IsOn = preferences.TrayCountdown;
        floatingTimer.IsOn = preferences.FloatingTimer;
        floatingStyle.SelectedIndex = (int)preferences.TimerStyle;
        floatingOnTop.IsOn = preferences.FloatingTimerOnTop;
        error.Message = "Couldn’t load the saved appearance. Return to Appearance to retry.";
        error.IsOpen = loadFailed;
        saveButton.IsEnabled = !loadFailed;
        theme.IsEnabled = !loadFailed;
        scene.IsEnabled = !loadFailed;
        motion.IsEnabled = !loadFailed;
        sound.IsEnabled = !loadFailed;
        trayCountdown.IsEnabled = !loadFailed;
        floatingTimer.IsEnabled = !loadFailed;
        UpdateFloatingControls();
    }

    private void UpdateFloatingControls()
    {
        floatingStyle.IsEnabled = floatingOnTop.IsEnabled = !loadFailed && floatingTimer.IsOn;
    }

    private static void LabelToggle(ToggleSwitch toggle, string label)
    {
        toggle.Header = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(toggle, label);
    }

    private static TextBlock Note(string text)
    {
        var label = SurfaceStyle.SecondaryLabel(text, SurfaceStyle.Body);
        label.TextWrapping = TextWrapping.Wrap;
        return label;
    }
}
