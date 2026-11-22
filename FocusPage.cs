using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class FocusPage : UserControl
{
    private readonly TextBlock phase = Label("", SurfaceStyle.SectionTitle, heading: AutomationHeadingLevel.Level2);
    private readonly TextBlock timer = SurfaceStyle.Timer("", 52);
    private readonly TextBlock pauses = Secondary("", SurfaceStyle.Body);
    private readonly TextBlock nextBreak = Label("", SurfaceStyle.Body);
    private readonly TextBlock cadence = Secondary("", SurfaceStyle.Body);
    private readonly TextBlock task = Label("", SurfaceStyle.SectionTitle);
    private readonly TextBlock nextStep = Secondary("", SurfaceStyle.Body);
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Grid cards = new();
    private readonly Border timerCard;
    private readonly Border taskCard;
    private readonly StackPanel workControls = new() { Spacing = SurfaceStyle.ControlGap };
    private readonly StackPanel breakControls = new() { Spacing = SurfaceStyle.ControlGap };
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

    internal FocusPage(Func<FocusAction, string?> execute, Action<ManagementPage> navigate, Action? openGuide = null)
    {
        timer.TextWrapping = TextWrapping.Wrap;
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
        taskCard = Card(Label("When you return", SurfaceStyle.StrongBody, AutomationHeadingLevel.Level2), task, nextStep,
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

        var body = new StackPanel { Spacing = SurfaceStyle.SectionGap, Margin = new Thickness(SurfaceStyle.PageInset), MaxWidth = 1100 };
        body.Children.Add(SurfaceStyle.PageTitle("Focus"));
        body.Children.Add(error);
        if (openGuide is not null)
        {
            var gettingStarted = new Button { Content = "Getting started" };
            gettingStarted.Click += (_, _) => openGuide();
            body.Children.Add(gettingStarted);
        }
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
            $"{(snapshot.NextBreakIsLong ? "long" : "short")} break · {ReminderDuration.Format(snapshot.UpcomingBreakSeconds)}");
        SetText(cadence, snapshot.LongBreaks
            ? $"{snapshot.CompletedShorts} of {snapshot.ShortsBeforeLong} short breaks completed"
            : "Long breaks off");
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
        SetButton(snooze, FocusAction.Snooze, $"Snooze {ReminderDuration.Format(snapshot.SnoozeSeconds)}");
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
        cards.ColumnSpacing = stack ? 0 : SurfaceStyle.SectionGap;
        cards.RowSpacing = stack ? SurfaceStyle.SectionGap : 0;
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
        var content = new StackPanel { Spacing = SurfaceStyle.ControlGap };
        foreach (var child in children)
        {
            content.Children.Add(child);
        }
        var panel = SurfaceStyle.Panel(content);
        panel.Padding = new Thickness(SurfaceStyle.PageInset);
        panel.VerticalAlignment = VerticalAlignment.Top;
        return panel;
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
