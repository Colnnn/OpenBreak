using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace OpenBreak;

internal sealed class CaptureWindow : Window
{
    private const string CaptureHint = "Enter to save · Esc to cancel";
    private const int CompactHeight = 190;
    private const int ExpandedHeight = 235;
    private readonly TextBox input;
    private readonly TextBox nextStep;
    private readonly Button addNextStep;
    private readonly TextBlock title;
    private readonly TextBlock hint;
    private readonly StackPanel body;
    private readonly Func<string, string?, bool> save;
    private readonly bool captureMotionStudy;
    private TextBox editingField;
    private Storyboard? captureEntrance;
    private bool captureMotionAllowed;
    private nint sourceWindow;
    private bool placed;
    private int windowWidth = 460;
    private int compactHeight = CompactHeight;
    private int expandedHeight = ExpandedHeight;
    private int WindowHeight => nextStep.Visibility == Visibility.Visible ? expandedHeight : compactHeight;

    internal CaptureWindow(Func<string, string?, bool> save, Action cancel, bool captureMotionStudy = false)
    {
        this.save = save;
        this.captureMotionStudy = captureMotionStudy;
        Title = "OpenBreak · Capture task";
        title = SurfaceStyle.Label("Capture a task", SurfaceStyle.SurfaceTitle, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        input = new TextBox
        {
            PlaceholderText = "What would you like to come back to?",
            MaxLength = TaskStore.TitleLimit,
        };
        AutomationProperties.SetName(input, "Task title");
        nextStep = new TextBox
        {
            Header = "Next step (optional)",
            MaxLength = TaskStore.NextStepLimit,
            AcceptsReturn = false,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(nextStep, "Next step (optional)");
        editingField = input;
        input.GotFocus += (_, _) => editingField = input;
        nextStep.GotFocus += (_, _) => editingField = nextStep;
        addNextStep = new Button
        {
            Content = new TextBlock { Text = "Add next step", TextWrapping = TextWrapping.Wrap },
        };
        AutomationProperties.SetName(addNextStep, "Add next step");
        addNextStep.Click += (_, _) => ExpandNextStep();
        hint = SurfaceStyle.SecondaryLabel(CaptureHint, SurfaceStyle.Caption);
        hint.TextWrapping = TextWrapping.Wrap;

        var saveButton = new Button
        {
            Content = "Save task",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
        {
            saveButton.Style = style;
        }
        saveButton.Click += (_, _) => Save();

        var footer = new Grid { ColumnSpacing = SurfaceStyle.ContentGap };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var instructions = new StackPanel { Spacing = SurfaceStyle.TightGap, VerticalAlignment = VerticalAlignment.Center };
        instructions.Children.Add(addNextStep);
        instructions.Children.Add(hint);
        footer.Children.Add(instructions);
        Grid.SetColumn(saveButton, 1);
        footer.Children.Add(saveButton);
        footer.SizeChanged += (_, args) =>
        {
            var stack = args.NewSize.Width < saveButton.DesiredSize.Width + 140;
            Grid.SetColumn(saveButton, stack ? 0 : 1);
            Grid.SetRow(saveButton, stack ? 1 : 0);
            Grid.SetColumnSpan(instructions, stack ? 2 : 1);
            footer.RowSpacing = stack ? SurfaceStyle.ControlGap : 0;
        };

        body = new StackPanel
        {
            Spacing = SurfaceStyle.ContentGap,
            Margin = new Thickness(SurfaceStyle.PageInset, SurfaceStyle.SectionGap, SurfaceStyle.PageInset, SurfaceStyle.SectionGap),
        };
        body.Children.Add(title);
        body.Children.Add(input);
        body.Children.Add(nextStep);
        body.Children.Add(footer);
        var panel = SurfaceStyle.Frame(SurfaceStyle.Scroll(body));
        panel.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                cancel();
            }
            else if (args.Key == VirtualKey.Enter && !args.Handled &&
                (input.FocusState != FocusState.Unfocused || nextStep.FocusState != FocusState.Unfocused))
            {
                args.Handled = true;
                Save();
            }
        }), true);
        Content = panel;
        SurfaceWindow.Configure(this, noActivate: false);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                FocusDraftField();
            }
        };
        input.Loaded += (_, _) => FocusDraftField();
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            cancel();
        };
    }

    internal bool TryShow(nint source)
    {
        StopCaptureEntrance();
        if (!TryPosition(source, compactHeight))
        {
            return false;
        }
        input.Text = "";
        nextStep.Text = "";
        nextStep.Visibility = Visibility.Collapsed;
        addNextStep.Visibility = Visibility.Visible;
        editingField = input;
        hint.Text = CaptureHint;
        ShowInput();
        return true;
    }

    internal bool BringBack(nint source)
    {
        StopCaptureEntrance();
        if (!TryPosition(source, WindowHeight))
        {
            return false;
        }
        ShowInput();
        return true;
    }

    private bool TryPosition(nint source, int height)
    {
        if (!SurfaceWindow.TryPlace(this, source, windowWidth, height, out _))
        {
            return false;
        }
        sourceWindow = source;
        placed = true;
        return true;
    }

    private void ShowInput()
    {
        SurfaceWindow.ShowAndActivate(this);
        FocusDraftField();
    }

    private void ExpandNextStep()
    {
        nextStep.Visibility = Visibility.Visible;
        addNextStep.Visibility = Visibility.Collapsed;
        if (placed && AppWindow.IsVisible)
        {
            TryPosition(sourceWindow, WindowHeight);
        }
        editingField = nextStep;
        FocusDraftField();
    }

    private void FocusDraftField()
    {
        var field = editingField;
        var start = field.SelectionStart;
        var length = field.SelectionLength;
        if (field.Focus(FocusState.Programmatic) && (field.SelectionStart != start || field.SelectionLength != length))
        {
            field.Select(start, length);
        }
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        captureMotionAllowed = state.AllowsCaptureMotion(captureMotionStudy);
        if (!captureMotionAllowed)
        {
            StopCaptureEntrance();
        }
        SurfaceStyle.ApplyContrast(title, state);
        SurfaceStyle.ApplyContrast(hint, state);
        var previousWidth = windowWidth;
        var previousHeight = WindowHeight;
        windowWidth = state.ScaleWidth(460);
        compactHeight = state.ScaleHeight(CompactHeight);
        expandedHeight = state.ScaleHeight(ExpandedHeight);
        if (placed && AppWindow.IsVisible && (previousWidth != windowWidth || previousHeight != WindowHeight))
        {
            TryPosition(sourceWindow, WindowHeight);
        }
    }

    internal void StartCaptureEntrance()
    {
        if (!captureMotionAllowed || !AppWindow.IsVisible ||
            body.XamlRoot is null || editingField.FocusState == FocusState.Unfocused)
        {
            return;
        }

        captureEntrance ??= CreateCaptureEntrance();
        captureEntrance.Begin();
    }

    internal void StopCaptureEntrance() => captureEntrance?.Stop();

    private Storyboard CreateCaptureEntrance()
    {
        body.RenderTransform = new TranslateTransform();
        var duration = new Duration(TimeSpan.FromMilliseconds(140));
        var fade = new DoubleAnimation { From = 0.8, To = 1, Duration = duration };
        Storyboard.SetTarget(fade, body);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var travel = new DoubleAnimation { From = 4, To = 0, Duration = duration };
        Storyboard.SetTarget(travel, body);
        Storyboard.SetTargetProperty(travel, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(fade);
        storyboard.Children.Add(travel);
        return storyboard;
    }

    private void Save()
    {
        StopCaptureEntrance();
        var value = input.Text.Trim();
        if (value.Length == 0)
        {
            hint.Text = "Enter a task title.";
            editingField = input;
            FocusDraftField();
            return;
        }

        if (!save(value, nextStep.Text))
        {
            hint.Text = "Couldn’t save. Try again.";
            FocusDraftField();
        }
    }
}

internal sealed class WarningWindow : Window
{
    private readonly TextBlock title;
    private readonly TextBlock detail;
    private readonly Button snoozeButton = new();
    private readonly StackPanel actions;
    private readonly Border indicator;
    private SurfacePlacement placement;
    private int windowWidth = 340;
    private int previewHeight = 94;
    private int scheduledHeight = 138;

    internal WarningWindow(Action snooze, Action skip)
    {
        Title = "OpenBreak · Preview warning";
        title = SurfaceStyle.Label("", SurfaceStyle.SectionTitle, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        detail = SurfaceStyle.SecondaryLabel("Your 10-second preview is about to begin.", SurfaceStyle.Body);
        detail.TextWrapping = TextWrapping.Wrap;
        SetSnoozeDuration(ReminderSettings.DefaultSnoozeSeconds);
        snoozeButton.Click += (_, _) => snooze();
        var skipButton = new Button { Content = "Skip break" };
        skipButton.Click += (_, _) => skip();
        actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = SurfaceStyle.ControlGap,
            Margin = new Thickness(0, SurfaceStyle.TightGap, 0, 0) };
        actions.Children.Add(snoozeButton);
        actions.Children.Add(skipButton);
        var text = new StackPanel { Spacing = SurfaceStyle.TightGap, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(detail);
        text.Children.Add(actions);

        var block = new Grid { ColumnSpacing = SurfaceStyle.ContentGap,
            Margin = new Thickness(SurfaceStyle.SectionGap, SurfaceStyle.ControlGap, SurfaceStyle.SectionGap, SurfaceStyle.ControlGap) };
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        block.ColumnDefinitions.Add(new ColumnDefinition());
        indicator = new Border { Background = SurfaceStyle.Brush(0x409D8D), CornerRadius = new CornerRadius(2) };
        block.Children.Add(indicator);
        Grid.SetColumn(text, 1);
        block.Children.Add(text);
        Content = SurfaceStyle.Frame(SurfaceStyle.Scroll(block));
        SurfaceWindow.Configure(this, noActivate: true);
    }

    internal bool TryShow(nint source, string? breakKind = null)
    {
        if (!TryPrepare(source, breakKind))
        {
            return false;
        }
        ShowPrepared();
        return true;
    }

    internal bool TryPrepare(nint source, string? breakKind = null)
    {
        var scheduled = breakKind is not null;
        if (!SurfaceWindow.TryPlace(this, source, windowWidth, scheduled ? scheduledHeight : previewHeight,
            out placement, atCorner: true))
        {
            return false;
        }
        actions.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;
        detail.Text = breakKind ?? "Your 10-second preview is about to begin.";
        Update(10);
        return true;
    }

    internal void ShowPrepared() => AppWindow.Show(activateWindow: false);

    internal void ShowSaveError(string message) => detail.Text = message;

    internal void SetSnoozeDuration(int seconds)
    {
        var label = $"Snooze {ReminderDuration.Format(seconds)}";
        if (!Equals(snoozeButton.Content, label))
        {
            snoozeButton.Content = label;
        }
    }

    internal bool PlacementIsCurrent => SurfaceWindow.IsCurrent(this, placement);
    internal string DescribePlacement() => SurfaceWindow.DescribePlacement(this, placement);

    internal void ApplyAppearance(AppearanceState state)
    {
        SurfaceStyle.ApplyContrast(title, state);
        SurfaceStyle.ApplyContrast(detail, state);
        indicator.Background = SurfaceStyle.Brush(state.HighContrast ? state.TextColor : 0x409D8D);
        actions.Orientation = state.TextScale >= 1.4 ? Orientation.Vertical : Orientation.Horizontal;
        var width = state.ScaleWidth(340);
        var shortHeight = state.ScaleHeight(94);
        var actionHeight = state.ScaleHeight(138);
        if (width == windowWidth && shortHeight == previewHeight && actionHeight == scheduledHeight)
        {
            return;
        }

        windowWidth = width;
        previewHeight = shortHeight;
        scheduledHeight = actionHeight;
        if (placement.Monitor != 0 && AppWindow.IsVisible &&
            !SurfaceWindow.TryRepositionWarning(this, placement, windowWidth,
                actions.Visibility == Visibility.Visible ? scheduledHeight : previewHeight, out placement))
        {
            AppWindow.Hide();
        }
    }

    internal void Update(int seconds)
    {
        var label = seconds == 1 ? "Break in 1 second" : $"Break in {seconds} seconds";
        if (title.Text != label)
        {
            title.Text = label;
        }
    }
}

internal sealed class BreakWindow : Window
{
    private readonly TextBlock clock;
    private readonly TextBlock title;
    private readonly TextBlock taskTitle;
    private readonly TextBlock taskNote;
    private readonly ScrollViewer compactTask;
    private readonly ScrollViewer boardView;
    private readonly TextBlock boardHeading;
    private readonly Grid body;
    private readonly Button end;
    private readonly Grid root;
    private readonly Border sceneHost;
    private readonly ScenePalette palette = new();
    private readonly SolidColorBrush laneBackground = SurfaceStyle.Brush(0xE4F0EB, 12);
    private readonly SolidColorBrush cardBackground = SurfaceStyle.Brush(0xE4F0EB, 22);
    private readonly SolidColorBrush cardStroke = SurfaceStyle.Brush(0, 0);
    private readonly List<(SolidColorBrush Brush, uint Color)> markers = [];
    private readonly Dictionary<TaskStatus, StackPanel> boardCards = [];
    private Grid boardColumns = null!;
    private Storyboard? sceneAnimation;
    private BreakScene scene;
    private double textScale = 1;
    private bool stackBoard;
    private bool animateScene;
    private bool shellFullscreenExcluded;
    private SurfacePlacement placement;

    internal void ShowSaveError(string message) => title.Text = message;

    internal BreakWindow(Action dismiss)
    {
        Title = "OpenBreak · Break preview";
        clock = SurfaceStyle.Timer("0:10", 84);
        clock.HorizontalAlignment = HorizontalAlignment.Center;
        clock.Foreground = palette.Foreground;
        AutomationProperties.SetHelpText(clock, "Time remaining");
        AutomationProperties.SetLiveSetting(clock, AutomationLiveSetting.Off);
        title = SurfaceStyle.Label("Time for a short break", SurfaceStyle.StrongBody);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Foreground = palette.Foreground;
        title.TextWrapping = TextWrapping.Wrap;

        var taskLabel = SurfaceStyle.Label("When you're back", SurfaceStyle.Caption);
        taskLabel.Foreground = palette.Secondary;
        taskTitle = SurfaceStyle.Label("", SurfaceStyle.SectionTitle, FontWeights.SemiBold);
        taskTitle.Foreground = palette.Foreground;
        taskTitle.TextWrapping = TextWrapping.Wrap;
        taskNote = SurfaceStyle.Label("", SurfaceStyle.Body);
        taskNote.Foreground = palette.Secondary;
        taskNote.TextWrapping = TextWrapping.Wrap;
        var taskBody = new StackPanel { Spacing = SurfaceStyle.TightGap };
        taskBody.Children.Add(taskLabel);
        taskBody.Children.Add(taskTitle);
        taskBody.Children.Add(taskNote);
        compactTask = BreakContent(palette.Card(taskBody));
        boardHeading = SurfaceStyle.Label("", SurfaceStyle.SectionTitle, FontWeights.SemiBold);
        boardHeading.Foreground = palette.Foreground;
        boardHeading.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(boardHeading, AutomationHeadingLevel.Level1);
        boardView = BreakContent(CreateBoard());
        boardView.Visibility = Visibility.Collapsed;

        end = new Button
        {
            Content = "End break",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, SurfaceStyle.SectionGap, 0, 0),
            Padding = new Thickness(SurfaceStyle.SectionGap, SurfaceStyle.ControlGap, SurfaceStyle.SectionGap, SurfaceStyle.ControlGap),
        };
        end.Click += (_, _) => dismiss();
        var heading = new StackPanel { Spacing = SurfaceStyle.TightGap };
        heading.Children.Add(clock);
        heading.Children.Add(title);

        body = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 410,
            Margin = new Thickness(SurfaceStyle.PageInset),
            RowSpacing = SurfaceStyle.TightGap,
        };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(heading);
        Grid.SetRow(compactTask, 1);
        body.Children.Add(compactTask);
        Grid.SetRow(boardView, 1);
        body.Children.Add(boardView);
        Grid.SetRow(end, 2);
        body.Children.Add(end);

        root = new Grid { Background = palette.Background };
        sceneHost = new Border { Child = StillBackdrop.Create(BreakScene.Dusk) };
        root.Children.Add(sceneHost);
        root.Children.Add(body);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                dismiss();
            }
        }), true);
        Content = root;
        SurfaceWindow.Configure(this, noActivate: false);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                end.Focus(FocusState.Programmatic);
            }
        };
        end.Loaded += (_, _) => end.Focus(FocusState.Programmatic);
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            dismiss();
        };
    }

    internal void SetPreview(BreakView view, bool longBreak = false)
    {
        Update(10);
        title.Text = longBreak ? "Time for a long break" : "Time for a short break";
        var compact = view.Layout == BreakLayout.Compact ? view.Task : null;
        taskTitle.Text = compact?.Title ?? "";
        taskNote.Text = compact?.NextStep ?? "";
        taskNote.Visibility = taskNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        compactTask.Visibility = compact is null ? Visibility.Collapsed : Visibility.Visible;

        foreach (var cards in boardCards.Values)
        {
            cards.Children.Clear();
        }
        var sample = view.Layout == BreakLayout.SampleBoard;
        var saved = view.Layout == BreakLayout.SavedBoard && view.Tasks is { Count: > 0 };
        if (sample)
        {
            PopulateSampleBoard();
        }
        else if (saved)
        {
            foreach (var card in view.Tasks!)
            {
                boardCards[card.Status].Children.Add(CreateBoardCard(card.Title, card.NextStep));
            }
        }

        var showBoard = sample || saved;
        boardHeading.Text = sample ? "Sample board" : saved ? "Saved task board" : "";
        AutomationProperties.SetName(boardView, boardHeading.Text);
        boardView.Visibility = showBoard ? Visibility.Visible : Visibility.Collapsed;
        var content = showBoard ? boardView : compactTask;
        content.ChangeView(null, 0, null, disableAnimation: true);
        body.HorizontalAlignment = showBoard ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        UpdateContentWidth();
    }

    internal bool HasForeground => Desktop.IsForeground(WinRT.Interop.WindowNative.GetWindowHandle(this));
    internal bool PlacementIsCurrent => AppWindow.IsVisible && SurfaceWindow.IsCurrent(this, placement);
    internal string DescribePlacement() =>
        $"{SurfaceWindow.DescribePlacement(this, placement)} shellFullscreenExcluded={shellFullscreenExcluded}";

    internal bool TryPrepareToShow(nint source)
    {
        if (!SurfaceWindow.TryPlaceBreak(this, source, out placement))
        {
            return false;
        }
        shellFullscreenExcluded = Desktop.TryExcludeShellFullscreen(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return shellFullscreenExcluded;
    }

    internal bool TryShow(nint source)
    {
        if (!TryPrepareToShow(source))
        {
            return false;
        }
        SurfaceWindow.ShowAndActivate(this);
        if (!PlacementIsCurrent)
        {
            HideAndReturn(source);
            return false;
        }
        end.Focus(FocusState.Programmatic);
        StartSceneEntrance();
        return true;
    }

    internal void HideAndReturn(nint source)
    {
        StopSceneEntrance();
        SurfaceWindow.HideAndReturn(this, source);
        ClearShellFullscreenExclusion();
    }

    internal void CloseForQuit()
    {
        ClearShellFullscreenExclusion();
        Close();
    }

    private void ClearShellFullscreenExclusion()
    {
        if (shellFullscreenExcluded)
        {
            Desktop.ClearShellFullscreenExclusion(WinRT.Interop.WindowNative.GetWindowHandle(this));
            shellFullscreenExcluded = false;
        }
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        StopSceneEntrance();
        animateScene = state.AnimateScene;
        root.RequestedTheme = state.HighContrast ? ElementTheme.Default : ElementTheme.Dark;
        palette.Apply(state);
        sceneHost.Visibility = state.ShowScene ? Visibility.Visible : Visibility.Collapsed;
        if (scene != state.Scene)
        {
            scene = state.Scene;
            sceneHost.Child = StillBackdrop.Create(scene);
        }

        laneBackground.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.WindowColor : 0x23393D);
        cardBackground.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.WindowColor : 0x314A4E);
        cardStroke.Color = SurfaceStyle.ColorFromRgb(state.TextColor, state.HighContrast ? (byte)255 : (byte)0);
        foreach (var (brush, color) in markers)
        {
            brush.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.TextColor : color);
        }
        textScale = state.TextScale;
        UpdateContentWidth();
        LayoutBoardColumns();
    }

    private void UpdateContentWidth()
    {
        var showBoard = boardView.Visibility == Visibility.Visible;
        body.MaxWidth = (showBoard ? 960 : 410) + 240 * (textScale - 1);
    }

    internal void StopSceneEntrance()
    {
        sceneAnimation?.Stop();
        sceneAnimation = null;
        sceneHost.Opacity = 1;
    }

    private void StartSceneEntrance()
    {
        StopSceneEntrance();
        if (!animateScene || sceneHost.XamlRoot is null)
        {
            return;
        }

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(250)) };
        Storyboard.SetTarget(fade, sceneHost);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sceneAnimation = new Storyboard();
        sceneAnimation.Children.Add(fade);
        sceneAnimation.Begin();
    }

    internal void Update(int seconds)
    {
        var value = $"{seconds / 60}:{seconds % 60:00}";
        if (clock.Text != value)
        {
            clock.Text = value;
        }
    }

    private static ScrollViewer BreakContent(UIElement content) => new()
    {
        Content = content,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Enabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsTabStop = true,
        Margin = new Thickness(0, SurfaceStyle.SectionGap, 0, 0),
    };

    private Border CreateBoard()
    {
        var lanes = new[]
        {
            CreateLane(TaskStatus.ToDo, "To do", 0x97C0EE),
            CreateLane(TaskStatus.InProgress, "In progress", 0xF2B462),
            CreateLane(TaskStatus.Done, "Done", 0x9ACBA7),
        };
        var columns = new Grid { ColumnSpacing = SurfaceStyle.ControlGap };
        boardColumns = columns;
        foreach (var lane in lanes)
        {
            Grid.SetColumn(lane, columns.Children.Count);
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.Children.Add(lane);
        }

        var board = new StackPanel { Spacing = SurfaceStyle.ContentGap };
        board.Children.Add(boardHeading);
        board.Children.Add(columns);

        columns.SizeChanged += (_, _) => LayoutBoardColumns();

        return palette.Card(board);
    }

    private void PopulateSampleBoard()
    {
        boardCards[TaskStatus.ToDo].Children.Add(CreateBoardCard("Draft the handover note"));
        boardCards[TaskStatus.ToDo].Children.Add(CreateBoardCard("Schedule the design review"));
        boardCards[TaskStatus.InProgress].Children.Add(CreateBoardCard("Check the quotation totals"));
        boardCards[TaskStatus.Done].Children.Add(CreateBoardCard("Send the drawings"));
    }

    private void LayoutBoardColumns()
    {
        var columns = boardColumns;
        var stack = columns.ActualWidth < 460 * textScale;
        if (stack == stackBoard)
        {
            return;
        }

        stackBoard = stack;
        columns.ColumnSpacing = stack ? 0 : SurfaceStyle.ControlGap;
        columns.RowSpacing = stack ? SurfaceStyle.ControlGap : 0;
        for (var index = 0; index < columns.Children.Count; index++)
        {
            var lane = (FrameworkElement)columns.Children[index];
            Grid.SetColumn(lane, stack ? 0 : index);
            Grid.SetColumnSpan(lane, stack ? columns.Children.Count : 1);
            Grid.SetRow(lane, stack ? index : 0);
        }
    }

    private Border CreateLane(TaskStatus laneStatus, string name, uint statusColor)
    {
        var label = SurfaceStyle.Label(name, SurfaceStyle.StrongBody, FontWeights.SemiBold);
        label.Foreground = palette.Foreground;
        label.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        var header = new Grid { ColumnSpacing = SurfaceStyle.ControlGap };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var marker = SurfaceStyle.Brush(statusColor);
        markers.Add((marker, statusColor));
        header.Children.Add(new Border
        {
            Background = marker,
            Width = 7,
            Height = 7,
            CornerRadius = SurfaceStyle.CardCorner,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(label, 1);
        header.Children.Add(label);

        var lane = new StackPanel { Spacing = SurfaceStyle.ControlGap };
        lane.Children.Add(header);
        var cards = new StackPanel { Spacing = SurfaceStyle.ControlGap };
        boardCards.Add(laneStatus, cards);
        lane.Children.Add(cards);

        return new Border
        {
            Background = laneBackground,
            BorderBrush = cardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = SurfaceStyle.PanelCorner,
            Padding = new Thickness(SurfaceStyle.ControlGap),
            Child = lane,
        };
    }

    private Border CreateBoardCard(string taskTitle, string? nextStep = null)
    {
        var task = SurfaceStyle.Label(taskTitle, SurfaceStyle.Body);
        task.Foreground = palette.Foreground;
        task.TextWrapping = TextWrapping.Wrap;
        var card = new StackPanel { Spacing = SurfaceStyle.TightGap };
        card.Children.Add(task);
        if (!string.IsNullOrEmpty(nextStep))
        {
            var note = SurfaceStyle.Label(nextStep, SurfaceStyle.Caption);
            note.Foreground = palette.Secondary;
            note.TextWrapping = TextWrapping.Wrap;
            card.Children.Add(note);
        }
        return new Border
        {
            Background = cardBackground,
            BorderBrush = cardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = SurfaceStyle.CardCorner,
            Padding = new Thickness(SurfaceStyle.ControlGap),
            Child = card,
        };
    }
}
