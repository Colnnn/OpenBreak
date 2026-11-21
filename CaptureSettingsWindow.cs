using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class CaptureSettingsWindow : Window
{
    private readonly TextBox chord = new() { Header = "Capture shortcut", MaxLength = 40 };
    private readonly TextBlock status = SurfaceStyle.SecondaryLabel("", 14);
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Button openGuide = new() { Content = "Getting started" };
    private readonly StackPanel guide = new() { Spacing = 10 };
    private readonly Func<CaptureShortcut, string?> apply;
    private bool placed;
    private bool quitting;

    internal CaptureSettingsWindow(Func<CaptureShortcut, string?> apply, Func<bool> dismissGuide)
    {
        this.apply = apply;
        Title = "Afterbreak · Capture settings";
        SystemBackdrop = new MicaBackdrop();

        var title = SurfaceStyle.Label("Capture settings", 27, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        status.TextWrapping = TextWrapping.Wrap;
        var applyButton = new Button { Content = "Apply shortcut" };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
        {
            applyButton.Style = style;
        }
        applyButton.Click += (_, _) => Apply();

        var guideTitle = SurfaceStyle.Label("Getting started", 19, FontWeights.SemiBold);
        guide.Children.Add(guideTitle);
        guide.Children.Add(BodyText("Press your capture shortcut or choose Capture task from the tray. Enter saves and Escape cancels."));
        guide.Children.Add(BodyText("Choose Open Tasks from the tray to edit and move your tasks. Scheduled breaks start off. Turn them on in Reminder settings."));
        guide.Children.Add(BodyText("Closing a window keeps Afterbreak in the tray. Choose Quit Afterbreak there to stop the app."));
        var dismiss = new Button { Content = "Got it" };
        dismiss.Click += (_, _) =>
        {
            if (dismissGuide())
            {
                error.IsOpen = false;
                ShowGuide(false);
                openGuide.Focus(FocusState.Programmatic);
            }
            else
            {
                Report("Couldn’t save this preference. Try again.");
            }
        };
        guide.Children.Add(dismiss);
        openGuide.Click += (_, _) =>
        {
            ShowGuide(true);
            dismiss.Focus(FocusState.Programmatic);
        };

        var form = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        form.Children.Add(title);
        form.Children.Add(status);
        form.Children.Add(chord);
        form.Children.Add(BodyText("Type a shortcut such as Ctrl+Shift+K. Use two or three modifiers (Ctrl, Alt, Shift) and a letter, digit or F1–F11."));
        form.Children.Add(applyButton);
        form.Children.Add(error);
        form.Children.Add(openGuide);
        form.Children.Add(guide);
        Content = new ScrollViewer { Content = form, HorizontalContentAlignment = HorizontalAlignment.Stretch };
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

    internal void Show(nint source, CapturePreferences preferences, string description)
    {
        var opening = !AppWindow.IsVisible;
        if (opening)
        {
            SetPreferences(preferences, description);
        }

        if (!placed)
        {
            SurfaceWindow.Place(this, source, 510, 620);
            placed = true;
        }
        Activate();
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }
        if (opening)
        {
            chord.Focus(FocusState.Programmatic);
            chord.SelectAll();
        }
    }

    internal void SetPreferences(CapturePreferences preferences, string description)
    {
        chord.Text = preferences.Shortcut.ToString();
        error.IsOpen = false;
        SetStatus(description);
        ShowGuide(!preferences.GuideDismissed);
    }

    internal void SetStatus(string description) => status.Text = description;

    internal void CloseForQuit()
    {
        quitting = true;
        Close();
    }

    private void Apply()
    {
        if (!CaptureShortcut.TryParse(chord.Text, out var shortcut))
        {
            Report("Enter two or three different modifiers and one supported key.");
            return;
        }

        if (apply(shortcut) is { } failure)
        {
            Report(failure);
            return;
        }
        chord.Text = shortcut.ToString();
        error.IsOpen = false;
    }

    private void ShowGuide(bool show)
    {
        guide.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        openGuide.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Report(string message)
    {
        error.Message = message;
        error.IsOpen = true;
    }

    private static TextBlock BodyText(string text)
    {
        var block = SurfaceStyle.SecondaryLabel(text, 14);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }
}
