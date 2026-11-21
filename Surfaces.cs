using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

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
        Title = "Afterbreak · Capture task";
        title = SurfaceStyle.Label("Capture a task", 23, FontWeights.SemiBold);
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
        hint = SurfaceStyle.SecondaryLabel(CaptureHint, 12);
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

        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var instructions = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
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
            footer.RowSpacing = stack ? 8 : 0;
        };

        body = new StackPanel { Spacing = 12, Margin = new Thickness(20, 17, 20, 17) };
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
    private readonly StackPanel actions;
    private readonly Border indicator;
    private SurfacePlacement placement;
    private int windowWidth = 340;
    private int previewHeight = 94;
    private int scheduledHeight = 138;

    internal WarningWindow(Action snooze, Action skip)
    {
        Title = "Afterbreak · Preview warning";
        title = SurfaceStyle.Label("", 20, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        detail = SurfaceStyle.SecondaryLabel("Your 10-second preview is about to begin.", 13);
        detail.TextWrapping = TextWrapping.Wrap;
        var snoozeButton = new Button { Content = "Snooze 5 min" };
        snoozeButton.Click += (_, _) => snooze();
        var skipButton = new Button { Content = "Skip break" };
        skipButton.Click += (_, _) => skip();
        actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(snoozeButton);
        actions.Children.Add(skipButton);
        var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(detail);
        text.Children.Add(actions);

        var block = new Grid { ColumnSpacing = 12, Margin = new Thickness(16, 10, 16, 10) };
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
        var scheduled = breakKind is not null;
        if (!SurfaceWindow.TryPlace(this, source, windowWidth, scheduled ? scheduledHeight : previewHeight,
            out placement, atCorner: true))
        {
            return false;
        }
        actions.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;
        detail.Text = breakKind ?? "Your 10-second preview is about to begin.";
        Update(10);
        AppWindow.Show(activateWindow: false);
        return true;
    }

    internal bool PlacementIsCurrent => SurfaceWindow.IsCurrent(this, placement);

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

internal enum BreakLayout
{
    Compact,
    SampleBoard,
}

internal sealed class BreakWindow : Window
{
    private readonly TextBlock clock;
    private readonly TextBlock title;
    private readonly TextBlock taskTitle;
    private readonly TextBlock taskNote;
    private readonly ScrollViewer compactTask;
    private readonly ScrollViewer sampleBoard;
    private readonly Grid body;
    private readonly Button end;
    private readonly Grid root;
    private readonly Border sceneHost;
    private readonly SolidColorBrush skyText = SurfaceStyle.Brush(0xF4F8F2);
    private readonly SolidColorBrush foreground = SurfaceStyle.Brush(0xF4F8F2);
    private readonly SolidColorBrush subdued = SurfaceStyle.Brush(0xC7D8CE);
    private readonly SolidColorBrush panelBackground = SurfaceStyle.Brush(0x182E34, 240);
    private readonly SolidColorBrush panelStroke = SurfaceStyle.Brush(0xABC6BB, 100);
    private readonly SolidColorBrush laneBackground = SurfaceStyle.Brush(0xE4F0EB, 12);
    private readonly SolidColorBrush cardBackground = SurfaceStyle.Brush(0xE4F0EB, 22);
    private readonly SolidColorBrush cardStroke = SurfaceStyle.Brush(0, 0);
    private readonly List<(SolidColorBrush Brush, uint Color)> markers = [];
    private Grid sampleColumns = null!;
    private Storyboard? sceneAnimation;
    private BreakScene scene;
    private double textScale = 1;
    private bool stackSample;
    private bool animateScene;
    private SurfacePlacement placement;

    internal BreakWindow(Action dismiss)
    {
        Title = "Afterbreak · Break preview";
        clock = SurfaceStyle.Label("0:10", 84, FontWeights.Light);
        clock.FontFamily = new FontFamily("Segoe UI Variable Display");
        Typography.SetNumeralAlignment(clock, FontNumeralAlignment.Tabular);
        clock.HorizontalAlignment = HorizontalAlignment.Center;
        clock.Foreground = skyText;
        AutomationProperties.SetHelpText(clock, "Time remaining");
        AutomationProperties.SetLiveSetting(clock, AutomationLiveSetting.Off);
        title = SurfaceStyle.Label("Time for a short break", 17);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Foreground = skyText;
        title.TextWrapping = TextWrapping.Wrap;

        var taskLabel = SurfaceStyle.Label("When you're back", 13);
        taskLabel.Foreground = subdued;
        taskTitle = SurfaceStyle.Label("", 21, FontWeights.SemiBold);
        taskTitle.Foreground = foreground;
        taskTitle.TextWrapping = TextWrapping.Wrap;
        taskNote = SurfaceStyle.Label("", 15);
        taskNote.Foreground = subdued;
        taskNote.TextWrapping = TextWrapping.Wrap;
        var taskBody = new StackPanel { Spacing = 5 };
        taskBody.Children.Add(taskLabel);
        taskBody.Children.Add(taskTitle);
        taskBody.Children.Add(taskNote);
        var taskCard = new Border
        {
            Background = panelBackground,
            BorderBrush = panelStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 13, 18, 15),
            Child = taskBody,
        };

        compactTask = BreakContent(taskCard);
        sampleBoard = BreakContent(CreateSampleBoard());
        sampleBoard.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(sampleBoard, "Sample board");

        end = new Button
        {
            Content = "End break",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 15, 0, 0),
            Padding = new Thickness(17, 7, 17, 7),
        };
        end.Click += (_, _) => dismiss();
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(clock);
        heading.Children.Add(title);

        body = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 410,
            MinWidth = 250,
            Margin = new Thickness(20),
            RowSpacing = 4,
        };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(heading);
        Grid.SetRow(compactTask, 1);
        body.Children.Add(compactTask);
        Grid.SetRow(sampleBoard, 1);
        body.Children.Add(sampleBoard);
        Grid.SetRow(end, 2);
        body.Children.Add(end);

        root = new Grid { Background = SurfaceStyle.Brush(0x172A32) };
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

    internal void SetPreview(BreakLayout layout, string? task, string? nextStep = null)
    {
        Update(10);
        var showBoard = layout == BreakLayout.SampleBoard;
        taskTitle.Text = showBoard ? "" : task ?? "";
        taskNote.Text = showBoard || task is null ? "" : nextStep ?? "";
        taskNote.Visibility = taskNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        compactTask.Visibility = showBoard || task is null ? Visibility.Collapsed : Visibility.Visible;
        sampleBoard.Visibility = showBoard ? Visibility.Visible : Visibility.Collapsed;
        var content = showBoard ? sampleBoard : compactTask;
        content.ChangeView(null, 0, null, disableAnimation: true);
        body.HorizontalAlignment = showBoard ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        UpdateContentWidth();
    }

    internal bool HasForeground => Desktop.IsForeground(WinRT.Interop.WindowNative.GetWindowHandle(this));
    internal bool PlacementIsCurrent => AppWindow.IsVisible && SurfaceWindow.IsCurrent(this, placement);

    internal bool TryShow(nint source, BreakLayout layout, string? task, string? nextStep = null, bool longBreak = false)
    {
        if (!SurfaceWindow.TryPlaceBreak(this, source, out placement))
        {
            return false;
        }
        title.Text = longBreak ? "Time for a long break" : "Time for a short break";
        SetPreview(layout, task, nextStep);
        Activate();
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }
        end.Focus(FocusState.Programmatic);
        StartSceneEntrance();
        return true;
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        StopSceneEntrance();
        animateScene = state.AnimateScene;
        root.RequestedTheme = state.HighContrast ? ElementTheme.Default : ElementTheme.Dark;
        root.Background = SurfaceStyle.Brush(state.HighContrast ? state.WindowColor : 0x172A32);
        sceneHost.Visibility = state.ShowScene ? Visibility.Visible : Visibility.Collapsed;
        if (scene != state.Scene)
        {
            scene = state.Scene;
            sceneHost.Child = StillBackdrop.Create(scene);
        }

        skyText.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.TextColor : 0xF4F8F2);
        foreground.Color = skyText.Color;
        subdued.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.TextColor : 0xC7D8CE);
        panelBackground.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.WindowColor : 0x182E34,
            state.UseMaterials ? (byte)240 : (byte)255);
        panelStroke.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.TextColor : 0xABC6BB,
            state.HighContrast ? (byte)255 : (byte)120);
        laneBackground.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.WindowColor : 0x23393D);
        cardBackground.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.WindowColor : 0x314A4E);
        cardStroke.Color = SurfaceStyle.ColorFromRgb(state.TextColor, state.HighContrast ? (byte)255 : (byte)0);
        foreach (var (brush, color) in markers)
        {
            brush.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.TextColor : color);
        }
        textScale = state.TextScale;
        UpdateContentWidth();
        LayoutSampleColumns();
    }

    private void UpdateContentWidth()
    {
        var showBoard = sampleBoard.Visibility == Visibility.Visible;
        body.MaxWidth = (showBoard ? 720 : 410) + 240 * (textScale - 1);
        body.MinWidth = showBoard ? 0 : 250;
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
        Margin = new Thickness(0, 19, 0, 0),
    };

    private Border CreateSampleBoard()
    {
        var heading = SurfaceStyle.Label("Sample board", 18, FontWeights.SemiBold);
        heading.Foreground = foreground;
        heading.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);

        var lanes = new[]
        {
            CreateLane("To do", 0x97C0EE, "Draft the handover note", "Schedule the design review"),
            CreateLane("In progress", 0xF2B462, "Check the quotation totals"),
            CreateLane("Done", 0x9ACBA7, "Send the drawings"),
        };
        var columns = new Grid { ColumnSpacing = 10 };
        sampleColumns = columns;
        foreach (var lane in lanes)
        {
            Grid.SetColumn(lane, columns.Children.Count);
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.Children.Add(lane);
        }

        var board = new StackPanel { Spacing = 12 };
        board.Children.Add(heading);
        board.Children.Add(columns);

        columns.SizeChanged += (_, _) => LayoutSampleColumns();

        return new Border
        {
            Background = panelBackground,
            BorderBrush = panelStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15),
            Child = board,
        };
    }

    private void LayoutSampleColumns()
    {
        var columns = sampleColumns;
        var stack = columns.ActualWidth < 460 * textScale;
        if (stack == stackSample)
        {
            return;
        }

        stackSample = stack;
        columns.ColumnSpacing = stack ? 0 : 10;
        columns.RowSpacing = stack ? 10 : 0;
        for (var index = 0; index < columns.Children.Count; index++)
        {
            var lane = (FrameworkElement)columns.Children[index];
            Grid.SetColumn(lane, stack ? 0 : index);
            Grid.SetColumnSpan(lane, stack ? columns.Children.Count : 1);
            Grid.SetRow(lane, stack ? index : 0);
        }
    }

    private Border CreateLane(string name, uint statusColor, params string[] samples)
    {
        var label = SurfaceStyle.Label(name, 15, FontWeights.SemiBold);
        label.Foreground = foreground;
        label.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var marker = SurfaceStyle.Brush(statusColor);
        markers.Add((marker, statusColor));
        header.Children.Add(new Border
        {
            Background = marker,
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(label, 1);
        header.Children.Add(label);

        var lane = new StackPanel { Spacing = 8 };
        lane.Children.Add(header);
        foreach (var sample in samples)
        {
            var task = SurfaceStyle.Label(sample, 15);
            task.Foreground = foreground;
            task.TextWrapping = TextWrapping.Wrap;
            lane.Children.Add(new Border
            {
                Background = cardBackground,
                BorderBrush = cardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 8, 10, 8),
                Child = task,
            });
        }

        return new Border
        {
            Background = laneBackground,
            BorderBrush = cardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Child = lane,
        };
    }
}

internal static class SurfaceStyle
{
    internal static TextBlock Label(string text, double size, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
    };

    internal static Color ColorFromRgb(uint rgb, byte opacity = 255) =>
        Color.FromArgb(opacity, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    internal static SolidColorBrush Brush(uint rgb, byte opacity = 255) => new(ColorFromRgb(rgb, opacity));

    internal static void ApplyContrast(TextBlock text, AppearanceState state)
    {
        if (state.HighContrast)
        {
            text.Foreground = Brush(state.TextColor);
        }
        else
        {
            text.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    internal static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    internal static TextBlock SecondaryLabel(string text, double size)
    {
        var label = Label(text, size);
        label.Style = (Style)Application.Current.Resources["AfterbreakSecondaryLabelStyle"];
        return label;
    }

    internal static Border Frame(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["AfterbreakSurfaceFrameStyle"],
        Child = child,
    };
}
