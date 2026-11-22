using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Afterbreak;

internal sealed class FloatingTimerWindow : Window
{
    private readonly TextBlock time = SurfaceStyle.Timer("", 30);
    private readonly TextBlock phase = SurfaceStyle.SecondaryLabel("", SurfaceStyle.Body);
    private readonly Border frame;
    private readonly Action hideForSession;
    private readonly Action<FloatingTimerPosition> savePosition;
    private readonly bool activationGuarded;
    private SurfacePlacement placement;
    private FloatingTimerPosition? position;
    private (int X, int Y)? dragOffset;
    private bool dragged;
    private bool topmost;
    private bool closingForQuit;
    private int width = 218;
    private int height = 94;

    internal FloatingTimerWindow(Action hideForSession, Action<FloatingTimerPosition> savePosition, FloatingTimerPosition? position)
    {
        this.hideForSession = hideForSession;
        this.savePosition = savePosition;
        this.position = position;
        Title = "Afterbreak · Floating timer";
        var hide = new Button
        {
            Content = "\u00d7",
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(hide, "Hide floating timer");
        ToolTipService.SetToolTip(hide, "Hide floating timer");
        hide.Click += (_, _) => hideForSession();

        phase.TextWrapping = TextWrapping.Wrap;
        var labels = new StackPanel { Spacing = SurfaceStyle.TightGap, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(time);
        labels.Children.Add(phase);
        var body = new Grid { Margin = new Thickness(SurfaceStyle.ContentGap), ColumnSpacing = SurfaceStyle.ControlGap };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(labels);
        Grid.SetColumn(hide, 1);
        body.Children.Add(hide);
        frame = SurfaceStyle.Frame(body);
        frame.PointerPressed += StartDrag;
        frame.PointerMoved += MoveDrag;
        frame.PointerReleased += (_, _) => FinishDrag();
        frame.PointerCaptureLost += (_, _) => FinishDrag();
        Content = frame;

        SurfaceWindow.Configure(this, noActivate: true, alwaysOnTop: false);
        activationGuarded = Desktop.GuardAgainstInputActivation(Handle);
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = !closingForQuit;
            if (!closingForQuit)
            {
                this.hideForSession();
            }
        };
    }

    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    internal void SetDisplay(CountdownDisplay display)
    {
        var clock = display.Time.Length == 0 ? "\u2014" : display.Time;
        if (time.Text != clock)
        {
            time.Text = clock;
        }
        if (phase.Text != display.Phase)
        {
            phase.Text = display.Phase;
        }
    }

    internal void Present(CountdownDisplay display, bool keepOnTop)
    {
        if (!activationGuarded)
        {
            return;
        }
        SetDisplay(display);
        if (topmost != keepOnTop && Desktop.SetTopmostWithoutActivation(Handle, keepOnTop))
        {
            topmost = keepOnTop;
        }
        if (dragOffset is not null)
        {
            return;
        }
        if (!EnsurePlacement())
        {
            Hide();
        }
        else if (!AppWindow.IsVisible)
        {
            AppWindow.Show(activateWindow: false);
        }
    }

    internal void DisplayChanged()
    {
        placement = default;
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        SurfaceStyle.ApplyContrast(time, state);
        SurfaceStyle.ApplyContrast(phase, state);
        var nextWidth = state.ScaleWidth(218);
        var nextHeight = state.ScaleHeight(94);
        if (nextWidth != width || nextHeight != height)
        {
            width = nextWidth;
            height = nextHeight;
            placement = default;
        }
    }

    private bool EnsurePlacement() => SurfaceWindow.IsCurrent(this, placement) ||
        SurfaceWindow.TryPlaceFloatingTimer(this, position, width, height, out placement);

    private void StartDrag(object sender, PointerRoutedEventArgs args)
    {
        if (dragOffset is null && args.GetCurrentPoint(frame).Properties.IsLeftButtonPressed &&
            Desktop.CursorPosition() is { } cursor && Desktop.WindowBounds(Handle) is { } bounds &&
            frame.CapturePointer(args.Pointer))
        {
            dragOffset = (cursor.X - bounds.X, cursor.Y - bounds.Y);
            dragged = false;
            args.Handled = true;
        }
    }

    private void MoveDrag(object sender, PointerRoutedEventArgs args)
    {
        if (dragOffset is { } offset && Desktop.CursorPosition() is { } cursor &&
            Desktop.WindowBounds(Handle) is { } oldBounds)
        {
            var target = oldBounds with { X = cursor.X - offset.X, Y = cursor.Y - offset.Y };
            if (target != oldBounds && Desktop.MoveWithoutActivation(Handle, target))
            {
                dragged = true;
            }
            args.Handled = true;
        }
    }

    private void FinishDrag()
    {
        if (dragOffset is null)
        {
            return;
        }
        dragOffset = null;
        frame.ReleasePointerCaptures();
        if (!dragged || Desktop.WindowBounds(Handle) is not { } bounds)
        {
            return;
        }

        var monitor = Desktop.MonitorForWindow(Handle);
        if (Desktop.MonitorName(monitor) is { } name && Desktop.MonitorArea(monitor, workArea: true) is { } area)
        {
            var next = new FloatingTimerPosition(name,
                MonitorPlacement.RelativeOffset(bounds.X, area.X, area.Width - bounds.Width, FloatingTimerPosition.Extent),
                MonitorPlacement.RelativeOffset(bounds.Y, area.Y, area.Height - bounds.Height, FloatingTimerPosition.Extent));
            if (next.IsValid && next != position)
            {
                position = next;
                savePosition(next);
            }
        }
        placement = default;
        if (!EnsurePlacement())
        {
            Hide();
        }
    }

    internal void Hide()
    {
        if (dragOffset is not null)
        {
            dragOffset = null;
            placement = default;
            frame.ReleasePointerCaptures();
        }
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
    }

    internal void CloseForQuit()
    {
        closingForQuit = true;
        Hide();
        Close();
    }
}
