using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Afterbreak;

internal static class SurfaceWindow
{
    internal static void Configure(Window window, bool noActivate)
    {
        var appWindow = window.AppWindow;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;
        if (noActivate)
        {
            Desktop.AvoidActivation(WinRT.Interop.WindowNative.GetWindowHandle(window));
        }
    }

    internal static void Place(Window window, nint source, int width, int height, bool atCorner = false)
    {
        if (Desktop.MonitorArea(source, workArea: true) is not { } area)
        {
            return;
        }

        var w = Math.Min((int)Math.Round(width * area.Scale), area.Right - area.Left);
        var h = Math.Min((int)Math.Round(height * area.Scale), area.Bottom - area.Top);
        var inset = (int)Math.Round(20 * area.Scale);
        var x = atCorner ? Math.Max(area.Left, area.Right - w - inset) : (area.Left + area.Right - w) / 2;
        var y = atCorner ? Math.Max(area.Top, area.Bottom - h - inset) : (area.Top + area.Bottom - h) / 2;
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    internal static void PlaceBreak(Window window, nint source)
    {
        if (Desktop.MonitorArea(source, workArea: false) is { } area)
        {
            window.AppWindow.MoveAndResize(
                new RectInt32(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top));
        }
    }

    internal static void ShowCapture(Window window)
    {
        window.Activate();
        if (!window.AppWindow.IsVisible)
        {
            window.AppWindow.Show();
        }
        Desktop.ReturnTo(WinRT.Interop.WindowNative.GetWindowHandle(window));
    }

    internal static void HideAndReturn(Window surface, nint source)
    {
        var ownedForeground = Desktop.IsForeground(WinRT.Interop.WindowNative.GetWindowHandle(surface));
        if (ownedForeground)
        {
            Desktop.ReturnTo(source);
        }
        surface.AppWindow.Hide();
        if (ownedForeground)
        {
            Desktop.ReturnTo(source);
        }
    }
}
