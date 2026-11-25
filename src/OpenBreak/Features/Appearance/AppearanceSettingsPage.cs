using Microsoft.UI.Xaml.Controls;

namespace OpenBreak;

internal sealed class AppearanceSettingsPage : UserControl
{
    private readonly ComboBox theme = new();
    private readonly ComboBox scene = new();
    private readonly ComboBox motion = new();
    private readonly ToggleSwitch sound = new();
    private readonly ToggleSwitch trayCountdown = new();
    private readonly ToggleSwitch floatingTimer = new();
    private readonly ComboBox floatingStyle = new();
    private readonly ToggleSwitch floatingOnTop = new();
    private readonly ScenePreview preview = new();
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly SettingsLayout layout = new("Appearance");
    private readonly Button saveButton = ManagementStyle.Button("Save appearance", "AppearanceSave", primary: true);
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
        floatingTimer.Toggled += (_, _) => UpdateFloatingControls();

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
        var discard = ManagementStyle.Button("Discard changes", "AppearanceDiscard", () => SetPreferences(saved, loadFailed));
        var app = SettingsLayout.Group("App appearance",
            layout.Row("App theme", theme, "AppearanceTheme"));
        var breakAppearance = SettingsLayout.Group("Break scenery and sound",
            layout.Row("Break scenery", scene, "AppearanceScene"),
            layout.Row("Break scene motion", motion, "AppearanceMotion"),
            layout.Row("Play a sound when a break starts", sound, "AppearanceSound",
                "Plays a short chime for scheduled breaks and break previews when enabled."));
        var sample = SettingsLayout.Group("Scene preview",
            ManagementStyle.BodyText("Sample timer and task. Windows contrast themes hide scenery."), preview);
        var countdown = SettingsLayout.Group("Countdown visibility",
            layout.Row("Show countdown in the system tray", trayCountdown, "AppearanceTrayCountdown"),
            layout.Row("Show a floating desktop timer", floatingTimer, "AppearanceFloatingTimer"),
            layout.Row("Floating timer style", floatingStyle, "AppearanceFloatingStyle"),
            layout.Row("Keep the floating timer on top", floatingOnTop, "AppearanceFloatingOnTop"));
        layout.Form.Children.Add(ManagementStyle.Card(app));
        layout.Form.Children.Add(ManagementStyle.Card(breakAppearance));
        layout.Form.Children.Add(ManagementStyle.Card(sample));
        layout.Form.Children.Add(ManagementStyle.Card(countdown));
        layout.SetFooter(SettingsLayout.Footer(error, saveButton, discard));
        Content = layout;
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
        layout.ApplyAppearance(state);
    }

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
}
