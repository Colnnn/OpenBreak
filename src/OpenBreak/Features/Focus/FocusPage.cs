using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace OpenBreak;

internal sealed class FocusPage : UserControl
{
    private readonly TextBlock phase = ManagementStyle.Label("", SurfaceStyle.Body, heading: AutomationHeadingLevel.Level2);
    private readonly TextBlock timer = SurfaceStyle.Timer("", ManagementStyle.TimerSize);
    private readonly TextBlock status = ManagementStyle.Label("", SurfaceStyle.Body, supporting: true);
    private readonly TextBlock nextBreak = ManagementStyle.Label("", SurfaceStyle.SectionTitle);
    private readonly TextBlock cadence = ManagementStyle.Label("", SurfaceStyle.Body, supporting: true);
    private readonly TextBlock task = ManagementStyle.Label("", SurfaceStyle.SectionTitle);
    private readonly TextBlock nextStep = ManagementStyle.Label("", SurfaceStyle.Body, supporting: true);
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly StackPanel body;
    private readonly FocusScene scene = new();
    private readonly StackPanel timerContent;
    private readonly Grid cards = new();
    private readonly Grid cadenceMarks = new() { ColumnSpacing = SurfaceStyle.TightGap, MaxWidth = 236,
        HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border hero;
    private readonly Border taskCard;
    private readonly Border breakCard;
    private readonly TextBlock breakHeading;
    private readonly TextBlock[] labels;
    private readonly Ellipse phaseDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button startStop;
    private readonly Button pauseResume;
    private readonly Button snooze;
    private readonly Button skip;
    private readonly Button preview;
    private readonly Style normal = SurfaceStyle.GetStyle("OpenBreakManagementButtonStyle");
    private readonly Style primary = SurfaceStyle.GetStyle("OpenBreakManagementPrimaryButtonStyle");
    private FocusAction startStopAction;
    private FocusAction pauseResumeAction;
    private FocusAction skipAction;
    private FocusAction previewAction;
    private FocusSnapshot? current;
    private double textScale = 1;

    internal FocusPage(Func<FocusAction, string?> execute, Action<ManagementPage> navigate, Action? openGuide = null)
    {
        timer.TextWrapping = TextWrapping.Wrap;
        timer.Style = SurfaceStyle.GetStyle("OpenBreakManagementLabelStyle");
        timer.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(timer, AutomationLiveSetting.Off);
        startStop = ActionButton("FocusStartStop", () => startStopAction, Symbol.Play);
        pauseResume = ActionButton("FocusPauseResume", () => pauseResumeAction, Symbol.Pause);
        snooze = ActionButton("FocusSnooze", () => FocusAction.Snooze);
        skip = ActionButton("FocusSkip", () => skipAction);
        preview = ActionButton("FocusPreview", () => previewAction);
        var controls = new ActionRow { Margin = new Thickness(0, SurfaceStyle.ContentGap, 0, 0) };
        foreach (var control in new[] { pauseResume, startStop, snooze, skip, preview })
        {
            controls.Children.Add(control);
        }

        var phaseRow = IconLabel(phaseDot, phase);
        timerContent = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = SurfaceStyle.ControlGap,
            MaxWidth = 560,
        };
        timerContent.SizeChanged += (_, args) => FitTimer(args.NewSize.Width);
        timerContent.Children.Add(phaseRow);
        timerContent.Children.Add(timer);
        timerContent.Children.Add(status);
        timerContent.Children.Add(controls);

        var heroLayout = new Grid { CornerRadius = new CornerRadius(15) };
        var heroContent = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        heroContent.Children.Add(timerContent);
        heroLayout.Children.Add(scene);
        heroLayout.Children.Add(heroContent);
        hero = new Border
        {
            Style = SurfaceStyle.GetStyle("OpenBreakFocusHeroStyle"),
            Child = heroLayout,
            MinHeight = 310,
            Padding = new Thickness(0),
        };

        var taskHeading = ManagementStyle.Label("When you return", SurfaceStyle.Body, heading: AutomationHeadingLevel.Level2);
        breakHeading = ManagementStyle.Label("Next break", SurfaceStyle.Body, heading: AutomationHeadingLevel.Level2);
        taskCard = DetailCard(Symbol.List, taskHeading, task, nextStep,
            ManagementStyle.Button("Open task board", "FocusTaskBoard", () => navigate(ManagementPage.Tasks), link: true));
        breakCard = DetailCard(Symbol.Clock, breakHeading, nextBreak, cadence,
            ManagementStyle.Button("Break settings", "FocusBreakSettings", () => navigate(ManagementPage.Breaks), link: true), cadenceMarks);
        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        cards.Children.Add(taskCard);
        cards.Children.Add(breakCard);

        body = new StackPanel { Spacing = ManagementStyle.SectionGap, MaxWidth = 1120 };
        var heading = ManagementStyle.Label("Focus", SurfaceStyle.PageTitleSize, heading: AutomationHeadingLevel.Level1);
        labels = [heading, phase, timer, status, taskHeading, task, nextStep, breakHeading, nextBreak, cadence];
        var header = new ActionRow();
        header.Children.Add(heading);
        if (openGuide is not null)
        {
            header.Children.Add(ManagementStyle.Button("Getting started", "FocusGettingStarted", openGuide, link: true));
        }
        body.Children.Add(header);
        body.Children.Add(error);
        body.Children.Add(hero);
        body.Children.Add(cards);
        var scroll = SurfaceStyle.Scroll(body);
        scroll.SizeChanged += (_, args) => LayoutPage(args.NewSize.Width);
        Content = scroll;
        LayoutPage(0);
        ActualThemeChanged += (_, _) => scene.ApplyTheme(ActualTheme);

        Button ActionButton(string id, Func<FocusAction> action, Symbol? icon = null)
        {
            var button = ManagementStyle.Button("", id, () => SetActionError(execute(action())));
            if (icon is Symbol symbol)
            {
                var glyph = new SymbolIcon(symbol);
                AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
                button.Content = glyph;
                button.MinWidth = 36;
                button.Padding = new Thickness(6);
            }
            return button;
        }
    }

    internal void SetSnapshot(FocusSnapshot snapshot)
    {
        if (current == snapshot)
        {
            return;
        }
        SetText(timer, FocusSnapshot.FormatTime(snapshot.PreviewPhase.HasValue ? snapshot.PreviewSeconds : snapshot.Seconds));
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
            _ when !snapshot.Enabled => "Ready to focus",
            _ => snapshot.Phase switch
            {
                ReminderPhase.Snoozed => "Break snoozed",
                ReminderPhase.Warning => "Break starts in",
                ReminderPhase.Break => snapshot.NextBreakIsLong ? "Long break" : "Short break",
                _ => "Work interval",
            },
        });
        var pauseDescription = PauseDescription(snapshot.PausedBy, snapshot.PreviewPhase.HasValue);
        var remindersOff = !snapshot.Enabled && !snapshot.RemindersUnavailable && !snapshot.PreviewPhase.HasValue;
        SetText(status, remindersOff ? "Scheduled reminders are off" +
            (pauseDescription.Length == 0 ? "" : $"\n{pauseDescription}") : pauseDescription);
        var running = snapshot.PreviewPhase.HasValue || snapshot.Enabled && !snapshot.RemindersUnavailable && snapshot.PausedBy == ReminderPause.None;
        phaseDot.Style = SurfaceStyle.GetStyle(running ? "OpenBreakFocusPhaseRunningDotStyle" : "OpenBreakFocusPhaseDotStyle");
        SetText(breakHeading, snapshot.Phase is ReminderPhase.Warning or ReminderPhase.Break ? "Scheduled break" : "Next break");
        SetText(nextBreak, $"{(snapshot.NextBreakIsLong ? "Long" : "Short")} break · {ReminderDuration.Format(snapshot.UpcomingBreakSeconds)}");
        SetText(cadence, snapshot.LongBreaks
            ? $"{snapshot.CompletedShorts} of {snapshot.ShortsBeforeLong} short breaks completed"
            : "Long breaks off");
        SetText(task, snapshot.TaskUnavailable ? "Saved tasks are unavailable" : snapshot.NextTask?.Title ?? "No saved task yet");
        SetText(nextStep, snapshot.NextTask?.NextStep ?? "");
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        nextStep.Visibility = nextStep.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateCadence(snapshot);

        startStopAction = snapshot.Enabled ? FocusAction.Stop : FocusAction.Start;
        pauseResumeAction = (snapshot.PausedBy & ReminderPause.Work) != 0 ? FocusAction.Resume : FocusAction.Pause;
        skipAction = snapshot.Phase == ReminderPhase.Break ? FocusAction.EndBreak : FocusAction.Skip;
        previewAction = snapshot.PreviewPhase.HasValue ? FocusAction.CancelPreview : FocusAction.Preview;
        SetButton(startStop, startStopAction, snapshot.Enabled ? "Stop focus" : "Start focus",
            alwaysVisible: true, symbol: snapshot.Enabled ? Symbol.Stop : Symbol.Play);
        SetButton(pauseResume, pauseResumeAction, pauseResumeAction == FocusAction.Pause ? "Pause work" : "Resume work",
            symbol: pauseResumeAction == FocusAction.Pause ? Symbol.Pause : Symbol.Play);
        SetButton(snooze, FocusAction.Snooze, $"Snooze {ReminderDuration.Format(snapshot.SnoozeSeconds)}");
        SetButton(skip, skipAction, skipAction == FocusAction.EndBreak ? "End break" : "Skip break");
        SetButton(preview, previewAction, snapshot.PreviewPhase.HasValue ? "Cancel preview" : "Preview a break", alwaysVisible: true);

        var primaryAction = snapshot.PreviewPhase.HasValue ? preview :
            !snapshot.Enabled ? startStop :
            pauseResume.IsEnabled ? pauseResume :
            snooze.IsEnabled ? snooze :
            skip.IsEnabled ? skip : null;
        foreach (var button in new[] { startStop, pauseResume, snooze, skip, preview })
        {
            button.Style = button == primaryAction ? primary : normal;
        }

        void SetButton(Button button, FocusAction action, string title, bool alwaysVisible = false, Symbol? symbol = null)
        {
            var available = (snapshot.Actions & action) != 0;
            if (symbol is Symbol glyph)
            {
                ((SymbolIcon)button.Content).Symbol = glyph;
                if (AutomationProperties.GetName(button) != title)
                {
                    AutomationProperties.SetName(button, title);
                    ToolTipService.SetToolTip(button, title);
                }
            }
            else
            {
                ManagementStyle.SetButtonText(button, title);
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
        LayoutPage(((ScrollViewer)Content).ActualWidth);
        FitTimer(timerContent.ActualWidth);
        scene.Visibility = state.ShowScene ? Visibility.Visible : Visibility.Collapsed;
        scene.ApplyTheme(ActualTheme);
        foreach (var label in labels)
        {
            SurfaceStyle.ApplyContrast(label, state);
        }
        foreach (var panel in new[] { hero, taskCard, breakCard })
        {
            if (state.HighContrast)
            {
                panel.Background = SurfaceStyle.Brush(state.WindowColor);
                panel.BorderBrush = SurfaceStyle.Brush(state.TextColor);
            }
            else
            {
                panel.ClearValue(Border.BackgroundProperty);
                panel.ClearValue(Border.BorderBrushProperty);
            }
        }
    }

    private void LayoutPage(double width)
    {
        body.Margin = ManagementStyle.PageMargin(width);
        var inset = body.Margin.Left;
        var wideHero = width >= 600 * textScale;
        timerContent.Margin = new Thickness(wideHero ? 40 : ManagementStyle.NarrowInset, ManagementStyle.SectionGap,
            wideHero ? 40 : ManagementStyle.NarrowInset, ManagementStyle.SectionGap);
        var stack = width - 2 * inset < 560 * textScale;
        cards.ColumnSpacing = stack ? 0 : SurfaceStyle.SectionGap;
        cards.RowSpacing = stack ? SurfaceStyle.SectionGap : 0;
        Grid.SetColumnSpan(taskCard, stack ? 2 : 1);
        Grid.SetColumnSpan(breakCard, stack ? 2 : 1);
        Grid.SetColumn(breakCard, stack ? 0 : 1);
        Grid.SetRow(breakCard, stack ? 1 : 0);
    }

    private void FitTimer(double width)
    {
        // Leave enough room for seven tabular characters at the longest supported interval (24:00:00).
        var available = width / textScale;
        timer.FontSize = Math.Clamp(Math.Floor(available / 4), 24, ManagementStyle.TimerSize);
    }

    private void UpdateCadence(FocusSnapshot snapshot)
    {
        var count = snapshot.LongBreaks ? snapshot.ShortsBeforeLong : 0;
        if (cadenceMarks.Children.Count != count)
        {
            cadenceMarks.Children.Clear();
            cadenceMarks.ColumnDefinitions.Clear();
            for (var index = 0; index < count; index++)
            {
                var mark = new Border { Height = 3, Width = 12 };
                AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
                cadenceMarks.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(mark, index);
                cadenceMarks.Children.Add(mark);
            }
        }
        for (var index = 0; index < count; index++)
        {
            ((Border)cadenceMarks.Children[index]).Style = SurfaceStyle.GetStyle(index < snapshot.CompletedShorts
                ? "OpenBreakFocusCadenceCompleteMarkStyle" : "OpenBreakFocusCadenceMarkStyle");
        }
        cadenceMarks.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Border DetailCard(Symbol symbol, TextBlock heading, TextBlock title, TextBlock detail, Button link, Grid? marks = null)
    {
        var icon = new SymbolIcon(symbol) { Style = SurfaceStyle.GetStyle("OpenBreakManagementIconStyle"),
            VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Spacing = SurfaceStyle.TightGap };
        content.Children.Add(title);
        content.Children.Add(detail);
        if (marks is not null)
        {
            content.Children.Add(marks);
        }
        var layout = new Grid { RowSpacing = SurfaceStyle.ContentGap };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(IconLabel(icon, heading));
        Grid.SetRow(content, 1);
        layout.Children.Add(content);
        Grid.SetRow(link, 2);
        layout.Children.Add(link);
        return new Border { Style = SurfaceStyle.GetStyle("OpenBreakManagementCardStyle"), Child = layout };
    }

    private static Grid IconLabel(FrameworkElement icon, TextBlock label)
    {
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        var row = new Grid { ColumnSpacing = SurfaceStyle.ControlGap };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(icon);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    private static void SetText(TextBlock label, string text)
    {
        if (label.Text != text)
        {
            label.Text = text;
        }
    }

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
