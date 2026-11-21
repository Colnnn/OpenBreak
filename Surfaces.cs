using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class CaptureWindow : Window
{
    private readonly TextBox input;
    private readonly TextBlock hint;
    private readonly Func<string, bool> save;

    internal CaptureWindow(Func<string, bool> save, Action cancel)
    {
        this.save = save;
        Title = "Afterbreak · Capture task";
        var title = SurfaceStyle.Label("Capture a task", 23, FontWeights.SemiBold);
        input = new TextBox
        {
            PlaceholderText = "What would you like to come back to?",
            MaxLength = TaskStore.TitleLimit,
        };
        AutomationProperties.SetName(input, "Task title");
        hint = SurfaceStyle.SecondaryLabel("Enter to save · Esc to cancel", 12);

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
        hint.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(hint);
        Grid.SetColumn(saveButton, 1);
        footer.Children.Add(saveButton);

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(20, 17, 20, 17) };
        body.Children.Add(title);
        body.Children.Add(input);
        body.Children.Add(footer);
        var panel = SurfaceStyle.Frame(body);
        panel.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                cancel();
            }
            else if (args.Key == VirtualKey.Enter && input.FocusState != FocusState.Unfocused && !args.Handled)
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
                input.Focus(FocusState.Programmatic);
            }
        };
        input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            cancel();
        };
    }

    internal void Show(nint source)
    {
        input.Text = "";
        hint.Text = "Enter to save · Esc to cancel";
        BringBack(source);
    }

    internal void BringBack(nint source)
    {
        SurfaceWindow.Place(this, source, 460, 165);
        SurfaceWindow.ShowCapture(this);
        input.Focus(FocusState.Programmatic);
    }

    private void Save()
    {
        var value = input.Text.Trim();
        if (value.Length == 0)
        {
            hint.Text = "Enter a task title.";
            input.Focus(FocusState.Programmatic);
            return;
        }

        if (!save(value))
        {
            hint.Text = "Couldn’t save. Try again.";
            input.Focus(FocusState.Programmatic);
        }
    }
}

internal sealed class WarningWindow : Window
{
    private readonly TextBlock title;

    internal WarningWindow()
    {
        Title = "Afterbreak · Preview warning";
        title = SurfaceStyle.Label("", 20, FontWeights.SemiBold);
        var detail = SurfaceStyle.SecondaryLabel("Your 10-second preview is about to begin.", 13);
        var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(detail);

        var block = new Grid { ColumnSpacing = 12, Margin = new Thickness(16, 10, 16, 10) };
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        block.ColumnDefinitions.Add(new ColumnDefinition());
        block.Children.Add(new Border { Background = SurfaceStyle.Brush(0x409D8D), CornerRadius = new CornerRadius(2) });
        Grid.SetColumn(text, 1);
        block.Children.Add(text);
        Content = SurfaceStyle.Frame(block);
        SurfaceWindow.Configure(this, noActivate: true);
    }

    internal void Show(nint source)
    {
        Update(10);
        SurfaceWindow.Place(this, source, 340, 94, atCorner: true);
        AppWindow.Show(activateWindow: false);
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
    private readonly TextBlock taskTitle;
    private readonly TextBlock taskNote;
    private readonly ScrollViewer compactTask;
    private readonly ScrollViewer sampleBoard;
    private readonly Grid body;
    private readonly Button end;

    internal BreakWindow(Action dismiss)
    {
        Title = "Afterbreak · Break preview";
        var foreground = SurfaceStyle.Brush(0xF4F8F2);
        var subdued = SurfaceStyle.Brush(0xC7D8CE);
        clock = SurfaceStyle.Label("0:10", 84, FontWeights.Light);
        clock.FontFamily = new FontFamily("Segoe UI Variable Display");
        Typography.SetNumeralAlignment(clock, FontNumeralAlignment.Tabular);
        clock.HorizontalAlignment = HorizontalAlignment.Center;
        clock.Foreground = foreground;
        AutomationProperties.SetName(clock, "Time remaining");
        var title = SurfaceStyle.Label("Time for a short break", 17);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Foreground = foreground;

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
            Background = SurfaceStyle.Brush(0x182E34, 240),
            BorderBrush = SurfaceStyle.Brush(0xABC6BB, 100),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 13, 18, 15),
            Child = taskBody,
        };

        compactTask = BreakContent(taskCard);
        sampleBoard = BreakContent(CreateSampleBoard(foreground));
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
            RequestedTheme = ElementTheme.Dark,
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

        var root = new Grid();
        root.Children.Add(StillBackdrop.Create());
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
        body.MaxWidth = showBoard ? 720 : 410;
        body.MinWidth = showBoard ? 0 : 250;
    }

    internal void Show(nint source, BreakLayout layout, string? task, string? nextStep = null)
    {
        SetPreview(layout, task, nextStep);
        SurfaceWindow.PlaceBreak(this, source);
        Activate();
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }
        end.Focus(FocusState.Programmatic);
    }

    internal void Update(int seconds)
    {
        var value = $"0:{seconds:00}";
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

    private static Border CreateSampleBoard(Brush foreground)
    {
        var heading = SurfaceStyle.Label("Sample board", 18, FontWeights.SemiBold);
        heading.Foreground = foreground;
        heading.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);

        var lanes = new[]
        {
            CreateLane("To do", 0x97C0EE, foreground, "Draft the handover note", "Schedule the design review"),
            CreateLane("In progress", 0xF2B462, foreground, "Check the quotation totals"),
            CreateLane("Done", 0x9ACBA7, foreground, "Send the drawings"),
        };
        var columns = new Grid { ColumnSpacing = 10 };
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

        var stacked = false;
        columns.SizeChanged += (_, args) =>
        {
            var useStack = args.NewSize.Width < 460;
            if (useStack == stacked)
            {
                return;
            }

            stacked = useStack;
            columns.ColumnSpacing = stacked ? 0 : 10;
            columns.RowSpacing = stacked ? 10 : 0;
            for (var index = 0; index < lanes.Length; index++)
            {
                var lane = lanes[index];
                Grid.SetColumn(lane, stacked ? 0 : index);
                Grid.SetColumnSpan(lane, stacked ? lanes.Length : 1);
                Grid.SetRow(lane, stacked ? index : 0);
            }
        };

        return new Border
        {
            Background = SurfaceStyle.Brush(0x182E34, 240),
            BorderBrush = SurfaceStyle.Brush(0xABC6BB, 100),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15),
            Child = board,
        };
    }

    private static Border CreateLane(string name, uint statusColor, Brush foreground, params string[] samples)
    {
        var label = SurfaceStyle.Label(name, 15, FontWeights.SemiBold);
        label.Foreground = foreground;
        label.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new Border
        {
            Background = SurfaceStyle.Brush(statusColor),
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
                Background = SurfaceStyle.Brush(0xE4F0EB, 22),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 8, 10, 8),
                Child = task,
            });
        }

        return new Border
        {
            Background = SurfaceStyle.Brush(0xE4F0EB, 12),
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

    internal static SolidColorBrush Brush(uint rgb, byte opacity = 255) =>
        new(Color.FromArgb(opacity, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

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
