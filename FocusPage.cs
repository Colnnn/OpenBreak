using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class FocusPage : UserControl
{
    private readonly TextBlock phase = Label("", 21, heading: AutomationHeadingLevel.Level2);
    private readonly TextBlock timer = Label("", 52);
    private readonly TextBlock pauses = Secondary("", 14);
    private readonly TextBlock nextBreak = Label("", 15);
    private readonly TextBlock cadence = Secondary("", 14);
    private readonly TextBlock task = Label("", 19);
    private readonly TextBlock nextStep = Secondary("", 15);
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Grid cards = new();
    private readonly Border timerCard;
    private readonly Border taskCard;
    private readonly StackPanel workControls = new() { Spacing = 8 };
    private readonly StackPanel breakControls = new() { Spacing = 8 };
    private readonly Button startStop;
    private readonly Button pauseResume;
    private readonly Button snooze;
    private readonly Button skip;
    private readonly Button preview;
    private FocusAction startStopAction;
    private FocusAction pauseResumeAction;
    private FocusAction skipAction;
    private FocusAction previewAction;
    private FocusSnapshot? current;
    private double textScale = 1;

    internal FocusPage(Func<FocusAction, string?> execute, Action<ManagementPage> navigate)
    {
        AutomationProperties.SetLiveSetting(timer, AutomationLiveSetting.Off);
        startStop = ActionButton(() => startStopAction);
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
        {
            startStop.Style = style;
        }
        pauseResume = ActionButton(() => pauseResumeAction);
        snooze = ActionButton(() => FocusAction.Snooze);
        skip = ActionButton(() => skipAction);
        preview = ActionButton(() => previewAction);
        workControls.Children.Add(startStop);
        workControls.Children.Add(pauseResume);
        breakControls.Children.Add(snooze);
        breakControls.Children.Add(skip);
        breakControls.Children.Add(preview);
        breakControls.Children.Add(Link("Break settings", ManagementPage.Breaks));

        timerCard = Card(phase, timer, pauses, nextBreak, cadence, workControls, breakControls);
        taskCard = Card(Label("When you return", 16, AutomationHeadingLevel.Level2), task, nextStep,
            Link("Open task board", ManagementPage.Tasks));
        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        cards.Children.Add(timerCard);
        cards.Children.Add(taskCard);
        cards.SizeChanged += (_, _) => LayoutCards();
        timerCard.SizeChanged += (_, _) => LayoutControls();
        LayoutCards();

        var body = new StackPanel { Spacing = 16, Margin = new Thickness(24), MaxWidth = 1100 };
        body.Children.Add(Label("Focus", 30, AutomationHeadingLevel.Level1));
        body.Children.Add(error);
        body.Children.Add(cards);
        Content = SurfaceStyle.Scroll(body);

        Button ActionButton(Func<FocusAction> action)
        {
            var button = new Button();
            button.Click += (_, _) => SetActionError(execute(action()));
            return button;
        }

        Button Link(string label, ManagementPage page)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => navigate(page);
            return button;
        }
    }

    internal void SetSnapshot(FocusSnapshot snapshot)
    {
        if (current == snapshot)
        {
            return;
        }
        SetText(timer, Duration(snapshot.PreviewPhase.HasValue ? snapshot.PreviewSeconds : snapshot.Seconds));
        if (current is { } previous && previous with { Seconds = snapshot.Seconds, PreviewSeconds = snapshot.PreviewSeconds } == snapshot)
        {
            current = snapshot;
            return;
        }
        current = snapshot;

        SetText(phase, snapshot.PreviewPhase switch
        {
            ReminderPhase.Warning => "Preview starts in",
            ReminderPhase.Break => "Preview break",
            _ when snapshot.RemindersUnavailable => "Reminders unavailable",
            _ when !snapshot.Enabled => "Ready to focus · Reminders off",
            _ => snapshot.Phase switch
            {
                ReminderPhase.Snoozed => "Break snoozed",
                ReminderPhase.Warning => "Break starts in",
                ReminderPhase.Break => snapshot.NextBreakIsLong ? "Long break" : "Short break",
                _ => "Work interval",
            },
        });
        SetText(pauses, PauseDescription(snapshot.PausedBy, snapshot.PreviewPhase.HasValue));
        SetText(nextBreak, $"{(snapshot.Phase is ReminderPhase.Warning or ReminderPhase.Break ? "Scheduled" : "Next")} " +
            $"{(snapshot.NextBreakIsLong ? "long" : "short")} break · {FriendlyDuration(snapshot.UpcomingBreakSeconds)}");
        SetText(cadence, $"{snapshot.CompletedShorts} of 3 short breaks completed");
        SetText(task, snapshot.TaskUnavailable ? "Saved tasks are unavailable" : snapshot.NextTask?.Title ?? "No saved task yet");
        SetText(nextStep, snapshot.NextTask?.NextStep ?? "");
        pauses.Visibility = pauses.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        nextStep.Visibility = nextStep.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        startStopAction = snapshot.Enabled ? FocusAction.Stop : FocusAction.Start;
        pauseResumeAction = (snapshot.PausedBy & ReminderPause.Work) != 0 ? FocusAction.Resume : FocusAction.Pause;
        skipAction = snapshot.Phase == ReminderPhase.Break ? FocusAction.EndBreak : FocusAction.Skip;
        previewAction = snapshot.PreviewPhase.HasValue ? FocusAction.CancelPreview : FocusAction.Preview;
        SetButton(startStop, startStopAction, snapshot.Enabled ? "Stop focus" : "Start focus", alwaysVisible: true);
        SetButton(pauseResume, pauseResumeAction, pauseResumeAction == FocusAction.Pause ? "Pause work" : "Resume work");
        SetButton(snooze, FocusAction.Snooze, "Snooze 5 minutes");
        SetButton(skip, skipAction, skipAction == FocusAction.EndBreak ? "End break" : "Skip break");
        SetButton(preview, previewAction, snapshot.PreviewPhase.HasValue ? "Cancel preview" : "Preview a break", alwaysVisible: true);

        void SetButton(Button button, FocusAction action, string title, bool alwaysVisible = false)
        {
            var available = (snapshot.Actions & action) != 0;
            if (!Equals(button.Content, title))
            {
                button.Content = title;
            }
            button.Visibility = available || alwaysVisible ? Visibility.Visible : Visibility.Collapsed;
            button.IsEnabled = available;
        }
    }

    internal void SetActionError(string? message)
    {
        error.Message = message ?? "";
        error.IsOpen = message is not null;
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        textScale = state.TextScale;
        LayoutCards();
    }

    private void LayoutCards()
    {
        var stack = cards.ActualWidth < 740 * textScale;
        cards.ColumnSpacing = stack ? 0 : 16;
        cards.RowSpacing = stack ? 16 : 0;
        Grid.SetColumnSpan(timerCard, stack ? 2 : 1);
        Grid.SetColumnSpan(taskCard, stack ? 2 : 1);
        Grid.SetColumn(taskCard, stack ? 0 : 1);
        Grid.SetRow(taskCard, stack ? 1 : 0);
        LayoutControls();
    }

    private void LayoutControls()
    {
        var width = timerCard.ActualWidth / textScale;
        timer.FontSize = width < 220 ? 28 : width < 280 ? 38 : 52;
        workControls.Orientation = width < 310 ? Orientation.Vertical : Orientation.Horizontal;
        breakControls.Orientation = width < 620 ? Orientation.Vertical : Orientation.Horizontal;
    }

    private static Border Card(params UIElement[] children)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var child in children)
        {
            content.Children.Add(child);
        }
        return new Border
        {
            Child = content,
            Style = (Style)Application.Current.Resources["AfterbreakTaskPanelStyle"],
            Padding = new Thickness(20),
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    private static TextBlock Label(string text, double size, AutomationHeadingLevel heading = AutomationHeadingLevel.None)
    {
        var label = SurfaceStyle.Label(text, size, FontWeights.SemiBold);
        label.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(label, heading);
        return label;
    }

    private static TextBlock Secondary(string text, double size)
    {
        var label = SurfaceStyle.SecondaryLabel(text, size);
        label.TextWrapping = TextWrapping.Wrap;
        return label;
    }

    private static void SetText(TextBlock label, string text)
    {
        if (label.Text != text)
        {
            label.Text = text;
        }
    }

    private static string Duration(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60:00}:{seconds % 60:00}";

    private static string FriendlyDuration(int seconds) => seconds % 60 == 0 ? $"{seconds / 60} min" :
        seconds < 60 ? $"{seconds} sec" : $"{seconds / 60} min {seconds % 60} sec";

    private static string PauseDescription(ReminderPause pauses, bool previewing)
    {
        if (pauses == ReminderPause.None)
        {
            return "";
        }
        var reasons = new List<string>();
        Add(ReminderPause.Work, "work paused");
        Add(ReminderPause.Manual, "presentation pause");
        Add(ReminderPause.Locked, "Windows locked");
        Add(ReminderPause.Suspended, "Windows suspended");
        Add(ReminderPause.Presentation, "Windows presentation or full screen");
        Add(ReminderPause.OutsideHours, "outside working hours");
        Add(ReminderPause.Interaction, previewing ? "preview in progress" : "capture, editing or tray menu");
        return $"Paused · {string.Join(" · ", reasons)}";

        void Add(ReminderPause flag, string description)
        {
            if ((pauses & flag) != 0)
            {
                reasons.Add(description);
            }
        }
    }
}
