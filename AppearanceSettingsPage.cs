using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class AppearanceSettingsPage : UserControl
{
    private readonly ComboBox theme = new() { Header = "App theme", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox scene = new() { Header = "Break scenery", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Button saveButton = new() { Content = "Save appearance", HorizontalAlignment = HorizontalAlignment.Right };
    private AppearancePreferences saved = AppearancePreferences.Default;
    private bool loadFailed;

    internal AppearanceSettingsPage(Func<AppearancePreferences, bool> save)
    {
        var title = SurfaceStyle.Label("Appearance", 27, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        foreach (var label in Enum.GetNames<AppearanceTheme>())
        {
            theme.Items.Add(label);
        }
        foreach (var label in Enum.GetNames<BreakScene>())
        {
            scene.Items.Add(label);
        }
        var note = SurfaceStyle.SecondaryLabel("Windows contrast themes use system colors and hide break scenery.", 14);
        note.TextWrapping = TextWrapping.Wrap;
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

        var form = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
        form.Children.Add(title);
        form.Children.Add(theme);
        form.Children.Add(scene);
        form.Children.Add(note);
        form.Children.Add(error);
        form.Children.Add(saveButton);
        form.Children.Add(discard);
        Content = SurfaceStyle.Scroll(form);
    }

    private AppearancePreferences Draft => new((AppearanceTheme)theme.SelectedIndex, (BreakScene)scene.SelectedIndex);
    internal bool IsDirty => Draft != saved;
    internal bool HasFocusedEditor => theme.IsDropDownOpen || scene.IsDropDownOpen;

    internal void SetPreferences(AppearancePreferences preferences, bool loadFailed = false)
    {
        saved = preferences;
        this.loadFailed = loadFailed;
        theme.SelectedIndex = (int)preferences.Theme;
        scene.SelectedIndex = (int)preferences.Scene;
        error.Message = "Couldn’t load the saved appearance. Return to Appearance to retry.";
        error.IsOpen = loadFailed;
        saveButton.IsEnabled = !loadFailed;
        theme.IsEnabled = !loadFailed;
        scene.IsEnabled = !loadFailed;
    }

}
