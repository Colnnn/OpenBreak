using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenBreak;

internal sealed class CaptureSettingsPage : UserControl
{
    private readonly TextBox chord = new() { MaxLength = 40 };
    private readonly TextBlock status = ManagementStyle.BodyText("");
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Button openGuide;
    private readonly Border guide;
    private readonly SettingsLayout layout = new("Capture");
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
        var applyButton = ManagementStyle.Button("Apply shortcut", "CaptureApply", Apply, primary: true);
        var discard = ManagementStyle.Button("Discard shortcut changes", "CaptureDiscard", () =>
        {
            chord.Text = savedShortcut.ToString();
            error.IsOpen = false;
        });

        var guideContent = SettingsLayout.Group("Getting started",
            ManagementStyle.BodyText("Set your work and break durations in Breaks."));
        guide = ManagementStyle.Card(guideContent);
        openGuide = ManagementStyle.Button("Getting started", "CaptureOpenGuide", () => OpenGuide(focus: true));
        if (navigate is not null)
        {
            guideBreaks = ManagementStyle.Button("Open Breaks", "CaptureGuideBreaks", () => navigate(ManagementPage.Breaks), link: true);
            guideContent.Children.Add(guideBreaks);
        }
        guideContent.Children.Add(ManagementStyle.BodyText("Choose Add task in Tasks to write a title and an optional next step. Press Enter to save or Escape to cancel. Use your capture shortcut to save a task from another app."));
        if (navigate is not null)
        {
            guideContent.Children.Add(ManagementStyle.Button("Open Tasks", "CaptureGuideTasks", () => navigate(ManagementPage.Tasks), link: true));
        }
        guideContent.Children.Add(layout.Row("Break display", breakDisplay, "CaptureGuideDisplay",
            "A 10-second warning comes before the 10-second preview."));
        var preview = ManagementStyle.Button("Save and preview", "CaptureGuidePreview", () =>
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
        }, primary: true);
        var discardDisplay = ManagementStyle.Button("Discard display change", "CaptureGuideDiscard", () =>
        {
            breakDisplay.SelectedIndex = (int)savedDisplay;
            guideError.IsOpen = false;
        });
        guideContent.Children.Add(SettingsLayout.Footer(guideError, preview, discardDisplay));
        guideContent.Children.Add(ManagementStyle.BodyText("When you’re ready for scheduled breaks, choose Start focus on the Focus page. Closing this window keeps OpenBreak running in the tray. Choose Quit OpenBreak from the tray to stop it."));
        var dismiss = ManagementStyle.Button("Got it", "CaptureGuideDismiss", () =>
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
        });
        guideContent.Children.Add(dismiss);

        var shortcut = SettingsLayout.Group("Shortcut", status,
            layout.Row("Capture shortcut", chord, "CaptureChord",
                "Type a shortcut such as Ctrl+Shift+K. Use two or three modifiers (Ctrl, Alt, Shift) and a letter, digit or F1–F11."));
        layout.Form.Children.Add(ManagementStyle.Card(shortcut));
        layout.Form.Children.Add(openGuide);
        layout.Form.Children.Add(guide);
        layout.SetFooter(SettingsLayout.Footer(error, applyButton, discard));
        Content = layout;
    }

    internal void ApplyAppearance(AppearanceState state) => layout.ApplyAppearance(state);

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
}
