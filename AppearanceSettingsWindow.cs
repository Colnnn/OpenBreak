using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class AppearanceSettingsWindow : Window
{
    private readonly ComboBox theme = new() { Header = "App theme", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox scene = new() { Header = "Break scenery", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Button saveButton = new() { Content = "Save appearance", HorizontalAlignment = HorizontalAlignment.Right };
    private bool placed;
    private bool quitting;

    internal AppearanceSettingsWindow(Func<AppearancePreferences, bool> save)
    {
        Title = "Afterbreak · Appearance";
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
            var selected = new AppearancePreferences((AppearanceTheme)theme.SelectedIndex, (BreakScene)scene.SelectedIndex);
            if (selected.IsValid && save(selected))
            {
                AppWindow.Hide();
            }
            else
            {
                error.Message = "Couldn’t save appearance. Your selections are still here. Try again.";
                error.IsOpen = true;
            }
        };

        var form = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
        form.Children.Add(title);
        form.Children.Add(theme);
        form.Children.Add(scene);
        form.Children.Add(note);
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

    internal void SetPreferences(AppearancePreferences preferences, bool loadFailed = false)
    {
        theme.SelectedIndex = (int)preferences.Theme;
        scene.SelectedIndex = (int)preferences.Scene;
        error.Message = "Couldn’t load the saved appearance. Reopen this window to retry.";
        error.IsOpen = loadFailed;
        saveButton.IsEnabled = !loadFailed;
    }

    internal void Show(nint source, AppearancePreferences preferences, bool loadFailed)
    {
        if (!AppWindow.IsVisible)
        {
            SetPreferences(preferences, loadFailed);
        }
        if (!placed)
        {
            SurfaceWindow.Place(this, source, 460, 450);
            placed = true;
        }
        Activate();
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }
    }

    internal void CloseForQuit()
    {
        quitting = true;
        Close();
    }
}
