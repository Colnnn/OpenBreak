using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class CaptureSettingsPage : UserControl
{
    private readonly TextBox chord = new() { Header = "Capture shortcut", MaxLength = 40 };
    private readonly TextBlock status = SurfaceStyle.SecondaryLabel("", SurfaceStyle.Body);
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Button openGuide = new() { Content = "Getting started" };
    private readonly StackPanel guide = new() { Spacing = SurfaceStyle.ContentGap };
    private readonly ComboBox breakDisplay = ReminderSettingsPage.CreateBreakDisplaySelector();
    private readonly InfoBar guideError = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Button? guideBreaks;
    private readonly Func<CaptureShortcut, string?> apply;
    private CaptureShortcut savedShortcut = CaptureShortcut.Default;
    private BreakDisplay savedDisplay;

    internal CaptureSettingsPage(Func<CaptureShortcut, string?> apply, Func<bool> dismissGuide,
        Func<BreakDisplay, string?> saveAndPreview, Action<ManagementPage>? navigate = null)
    {
        this.apply = apply;
        var title = SurfaceStyle.PageTitle("Capture");
        status.TextWrapping = TextWrapping.Wrap;
        var applyButton = new Button { Content = "Apply shortcut" };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
        {
            applyButton.Style = style;
        }
        applyButton.Click += (_, _) => Apply();
        var discard = new Button { Content = "Discard shortcut changes" };
        discard.Click += (_, _) =>
        {
            chord.Text = savedShortcut.ToString();
            error.IsOpen = false;
        };

        var guideTitle = SurfaceStyle.Label("Getting started", SurfaceStyle.SectionTitle, FontWeights.SemiBold);
        guideTitle.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(guideTitle, AutomationHeadingLevel.Level2);
        guide.Children.Add(guideTitle);
        guide.Children.Add(BodyText("Set your work and break durations in Breaks."));
        if (navigate is not null)
        {
            guideBreaks = GuideLink("Open Breaks", ManagementPage.Breaks);
            guide.Children.Add(guideBreaks);
        }
        guide.Children.Add(BodyText("Choose Add task in Tasks to write a title and an optional next step. Press Enter to save or Escape to cancel. Use your capture shortcut to save a task from another app."));
        if (navigate is not null)
        {
            guide.Children.Add(GuideLink("Open Tasks", ManagementPage.Tasks));
        }
        guide.Children.Add(breakDisplay);
        guide.Children.Add(BodyText("A 10-second warning comes before the 10-second preview."));
        var preview = new Button { Content = "Save and preview" };
        preview.Click += (_, _) =>
        {
            if (saveAndPreview((BreakDisplay)breakDisplay.SelectedIndex) is { } failure)
            {
                guideError.Message = failure;
                guideError.IsOpen = true;
            }
            else
            {
                guideError.IsOpen = false;
            }
        };
        guide.Children.Add(preview);
        guide.Children.Add(guideError);
        var discardDisplay = new Button { Content = "Discard display change" };
        discardDisplay.Click += (_, _) =>
        {
            breakDisplay.SelectedIndex = (int)savedDisplay;
            guideError.IsOpen = false;
        };
        guide.Children.Add(discardDisplay);
        guide.Children.Add(BodyText("When you’re ready for scheduled breaks, choose Start focus on the Focus page. Closing this window keeps Afterbreak running in the tray. Choose Quit Afterbreak from the tray to stop it."));
        var dismiss = new Button { Content = "Got it" };
        dismiss.Click += (_, _) =>
        {
            if (dismissGuide())
            {
                error.IsOpen = false;
                ShowGuide(false);
                FocusIfAllowed(openGuide);
            }
            else
            {
                Report("Couldn’t save this preference. Try again.");
            }
        };
        guide.Children.Add(dismiss);
        openGuide.Click += (_, _) => OpenGuide(focus: true);

        var form = new StackPanel { Spacing = SurfaceStyle.ContentGap, Margin = new Thickness(SurfaceStyle.PageInset) };
        form.Children.Add(title);
        form.Children.Add(status);
        form.Children.Add(chord);
        form.Children.Add(BodyText("Type a shortcut such as Ctrl+Shift+K. Use two or three modifiers (Ctrl, Alt, Shift) and a letter, digit or F1–F11."));
        form.Children.Add(applyButton);
        form.Children.Add(discard);
        form.Children.Add(error);
        form.Children.Add(openGuide);
        form.Children.Add(guide);
        Content = SurfaceStyle.Scroll(form);

        Button GuideLink(string label, ManagementPage page)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => navigate?.Invoke(page);
            return button;
        }
    }

    internal bool GuideVisible => guide.Visibility == Visibility.Visible;
    internal bool IsDirty => chord.Text != savedShortcut.ToString() ||
        (GuideVisible && breakDisplay.SelectedIndex != (int)savedDisplay);
    internal bool HasFocusedEditor => breakDisplay.IsDropDownOpen;
    internal Func<bool>? CanRestoreFocus { private get; set; }

    internal void OpenGuide(bool focus = false)
    {
        ShowGuide(true);
        if (focus)
        {
            FocusIfAllowed(guideBreaks is { } link ? link : breakDisplay);
        }
    }

    private void FocusIfAllowed(Control target)
    {
        if (CanRestoreFocus?.Invoke() == true)
        {
            target.Focus(FocusState.Programmatic);
        }
    }

    internal void SetPreferences(CapturePreferences preferences, string description, BreakDisplay display)
    {
        savedShortcut = preferences.Shortcut;
        savedDisplay = display;
        chord.Text = savedShortcut.ToString();
        error.IsOpen = false;
        guideError.IsOpen = false;
        breakDisplay.SelectedIndex = (int)display;
        SetStatus(description);
        ShowGuide(!preferences.GuideDismissed);
    }

    internal void SetStatus(string description) => status.Text = description;

    internal void RebaseBreakDisplay(BreakDisplay display)
    {
        if (breakDisplay.SelectedIndex == (int)savedDisplay)
        {
            breakDisplay.SelectedIndex = (int)display;
        }
        savedDisplay = display;
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
        savedShortcut = shortcut;
        chord.Text = savedShortcut.ToString();
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
        var block = SurfaceStyle.SecondaryLabel(text, SurfaceStyle.Body);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }
}
