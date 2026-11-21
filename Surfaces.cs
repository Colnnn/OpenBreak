using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private readonly Action<string> save;

    internal CaptureWindow(Action<string> save, Action cancel)
    {
        this.save = save;
        Title = "Afterbreak · Capture task";
        var title = SurfaceStyle.Label("Capture a task", 23, FontWeights.SemiBold);
        input = new TextBox
        {
            PlaceholderText = "What would you like to come back to?",
            MaxLength = 140,
        };
        AutomationProperties.SetName(input, "Task title");
        hint = SurfaceStyle.Label("Enter to save · Esc to cancel", 12);
        if (SurfaceStyle.ThemeBrush("TextFillColorSecondaryBrush") is { } secondary)
        {
            hint.Foreground = secondary;
        }

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

        save(value);
    }
}

internal sealed class WarningWindow : Window
{
    private readonly TextBlock title;

    internal WarningWindow()
    {
        Title = "Afterbreak · Preview warning";
        title = SurfaceStyle.Label("", 20, FontWeights.SemiBold);
        var detail = SurfaceStyle.Label("Your 10-second preview is about to begin.", 13);
        if (SurfaceStyle.ThemeBrush("TextFillColorSecondaryBrush") is { } secondary)
        {
            detail.Foreground = secondary;
        }
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

internal sealed class BreakWindow : Window
{
    private readonly TextBlock clock;
    private readonly TextBlock taskTitle;
    private readonly Border taskCard;
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
        var taskBody = new StackPanel { Spacing = 5 };
        taskBody.Children.Add(taskLabel);
        taskBody.Children.Add(taskTitle);
        taskCard = new Border
        {
            Background = SurfaceStyle.Brush(0x182E34, 240),
            BorderBrush = SurfaceStyle.Brush(0xABC6BB, 100),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 13, 18, 15),
            Margin = new Thickness(0, 19, 0, 0),
            Child = taskBody,
        };

        end = new Button
        {
            Content = "End break",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 15, 0, 0),
            Padding = new Thickness(17, 7, 17, 7),
        };
        end.Click += (_, _) => dismiss();
        var body = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 410,
            MinWidth = 250,
            Margin = new Thickness(20),
            Spacing = 4,
            RequestedTheme = ElementTheme.Dark,
        };
        body.Children.Add(clock);
        body.Children.Add(title);
        body.Children.Add(taskCard);
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

    internal void Show(nint source, string? task)
    {
        Update(10);
        taskTitle.Text = task ?? "";
        taskCard.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
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

    internal static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    internal static Border Frame(UIElement child) => new()
    {
        Background = ThemeBrush("ApplicationPageBackgroundThemeBrush") ??
            new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)),
        BorderBrush = ThemeBrush("ControlStrokeColorDefaultBrush") ?? Brush(0x728D89),
        BorderThickness = new Thickness(1),
        Child = child,
    };
}
