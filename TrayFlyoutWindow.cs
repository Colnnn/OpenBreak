using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Afterbreak;

internal sealed class TrayFlyoutWindow : Window
{
    private sealed record ActionRow(Button Button, TextBlock Label)
    {
        internal TrayMenuItem Item { get; set; }
    }

    private readonly StackPanel body = new() { Margin = new Thickness(SurfaceStyle.ControlGap), Spacing = 2 };
    private readonly TextBlock status;
    private readonly TextBlock countdown;
    private readonly ScrollViewer scroll;
    private readonly Dictionary<TrayCommand, ActionRow> rows = [];
    private Action<bool>? dismiss;
    private bool activated;
    private bool closingForQuit;
    private TrayMenuState menuState;
    private PixelRect anchor;
    private SurfacePlacement placement;
    private AppearanceState appearance = AppearancePolicy.Resolve(AppearancePreferences.Default,
        new AppearanceSystem(false, false, false, 1, 0xFFFFFF, 0));
    private int VisibleCount => rows.Values.Count(row => row.Item.Visible);
    private int Width => appearance.ScaleWidth(324);
    private int Height => appearance.ScaleHeight(56 + VisibleCount * 38 +
        (countdown.Visibility == Visibility.Visible ? 26 : 0));

    internal TrayFlyoutWindow()
    {
        Title = "Afterbreak";
        status = SurfaceStyle.SecondaryLabel("", SurfaceStyle.Caption);
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = RowInsets();
        countdown = SurfaceStyle.SecondaryLabel("", SurfaceStyle.Caption);
        countdown.TextWrapping = TextWrapping.Wrap;
        countdown.Margin = RowInsets();
        scroll = SurfaceStyle.Scroll(body);
        var frame = SurfaceStyle.Frame(scroll);
        frame.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        frame.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        Content = frame;
        SurfaceWindow.Configure(this, noActivate: false);

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                if (activated && !HasForeground)
                {
                    dismiss?.Invoke(false);
                }
            }
            else
            {
                activated = true;
            }
        };
        body.Loaded += (_, _) =>
        {
            if (dismiss is not null && HasForeground)
            {
                FocusFirst();
            }
        };
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = !closingForQuit;
            if (!closingForQuit)
            {
                dismiss?.Invoke(true);
            }
        };
    }

    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    internal bool HasForeground => Desktop.IsForeground(Handle);
    internal bool PlacementIsCurrent => AppWindow.IsVisible && SurfaceWindow.IsCurrent(this, placement);

    internal void Prepare(TrayMenuState state, Action<TrayMenuItem> select, Action<bool> dismiss)
    {
        this.dismiss = dismiss;
        activated = false;
        body.Children.Clear();
        rows.Clear();
        foreach (var item in state.Items())
        {
            if (item.Command == TrayCommand.Settings)
            {
                body.Children.Add(new MenuFlyoutSeparator());
                body.Children.Add(status);
                body.Children.Add(countdown);
            }
            else if (item.Command == TrayCommand.Quit)
            {
                body.Children.Add(new MenuFlyoutSeparator());
            }

            var label = SurfaceStyle.Label("", SurfaceStyle.Body);
            label.TextWrapping = TextWrapping.Wrap;
            var button = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 36,
                Padding = RowInsets(),
            };
            if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtle) && subtle is Style style)
            {
                button.Style = style;
            }
            var row = new ActionRow(button, label);
            button.Click += (_, _) =>
            {
                if (row.Item.Selectable)
                {
                    select(row.Item);
                }
            };
            rows.Add(item.Command, row);
            body.Children.Add(button);
        }
        SetMenuState(state);
        scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static Thickness RowInsets() =>
        new(SurfaceStyle.ControlGap, SurfaceStyle.TightGap, SurfaceStyle.ControlGap, SurfaceStyle.TightGap);

    internal void UpdateMenu(TrayMenuState state)
    {
        if (menuState == state)
        {
            return;
        }

        if (menuState with { CountdownStatus = state.CountdownStatus } == state &&
            (menuState.CountdownStatus is null) == (state.CountdownStatus is null))
        {
            menuState = state;
            countdown.Text = state.CountdownStatus ?? "";
            return;
        }

        var previousHeight = Height;
        var focused = GetFocusedAction();
        SetMenuState(state);
        if (focused is not null && !focused.Item.Selectable && HasForeground)
        {
            FocusFirst();
        }
        if (previousHeight != Height)
        {
            RepositionIfVisible();
        }
    }

    private void SetMenuState(TrayMenuState state)
    {
        menuState = state;
        status.Text = state.ReminderStatus;
        countdown.Text = state.CountdownStatus ?? "";
        countdown.Visibility = state.CountdownStatus is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in state.Items())
        {
            var row = rows[item.Command];
            row.Item = item;
            row.Label.Text = item.Label;
            row.Button.Visibility = item.Visible ? Visibility.Visible : Visibility.Collapsed;
            row.Button.IsEnabled = item.Enabled;
        }
    }

    internal bool TryPlace(PixelRect trayAnchor)
    {
        anchor = trayAnchor;
        return SurfaceWindow.TryPlaceTray(this, anchor, Width, Height, out placement);
    }

    internal bool TryShow()
    {
        SurfaceWindow.ShowAndActivate(this);
        if (!HasForeground || dismiss is null || !PlacementIsCurrent)
        {
            return false;
        }

        activated = true;
        FocusFirst();
        return true;
    }

    internal void Hide()
    {
        dismiss = null;
        activated = false;
        placement = default;
        AppWindow.Hide();
    }

    internal void CloseForQuit()
    {
        closingForQuit = true;
        Close();
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        SurfaceStyle.ApplyContrast(status, state);
        SurfaceStyle.ApplyContrast(countdown, state);
        var previousWidth = Width;
        var previousHeight = Height;
        appearance = state;
        if (previousWidth != Width || previousHeight != Height)
        {
            RepositionIfVisible();
        }
    }

    private void RepositionIfVisible()
    {
        if (placement.Monitor != 0 && AppWindow.IsVisible && !TryPlace(anchor))
        {
            dismiss?.Invoke(false);
        }
    }

    private ActionRow? GetFocusedAction()
    {
        var focused = body.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        return rows.Values.FirstOrDefault(row => ReferenceEquals(row.Button, focused));
    }

    private void FocusFirst() => rows.Values.FirstOrDefault(row => row.Item.Selectable)?.Button.Focus(FocusState.Keyboard);

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            dismiss?.Invoke(true);
        }
        else if (args.Key is VirtualKey.Up or VirtualKey.Down)
        {
            args.Handled = true;
            var available = rows.Values.Where(row => row.Item.Selectable).ToArray();
            if (available.Length == 0)
            {
                return;
            }
            var current = Array.IndexOf(available, GetFocusedAction());
            var next = current < 0 ? (args.Key == VirtualKey.Down ? 0 : available.Length - 1) :
                (current + available.Length + (args.Key == VirtualKey.Down ? 1 : -1)) % available.Length;
            available[next].Button.Focus(FocusState.Keyboard);
        }
    }
}
