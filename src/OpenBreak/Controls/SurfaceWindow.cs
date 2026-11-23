using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace OpenBreak;

internal static class SurfaceWindow
{
    internal static void Configure(Window window, bool noActivate, bool alwaysOnTop = true)
    {
        var appWindow = window.AppWindow;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = alwaysOnTop;
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;
        if (noActivate)
        {
            Desktop.AvoidActivation(WinRT.Interop.WindowNative.GetWindowHandle(window));
        }
    }

    internal static bool TryPlace(Window window, nint source, int widthDip, int heightDip, out SurfacePlacement placement, bool atCorner = false)
    {
        placement = default;
        if (DisplayFor(source, workArea: true) is not { } display)
        {
            return false;
        }
        return TryPlaceIn(window, display.Monitor, display.Area, widthDip, heightDip, out placement, atCorner);
    }

    internal static bool TryRepositionWarning(Window window, SurfacePlacement previous, int widthDip, int heightDip, out SurfacePlacement placement)
    {
        placement = default;
        return Desktop.MonitorArea(previous.Monitor, workArea: true) is { } area &&
            TryPlaceIn(window, previous.Monitor, area, widthDip, heightDip, out placement, atCorner: true);
    }

    internal static bool TryPlaceTray(Window window, PixelRect anchor, int widthDip, int heightDip, out SurfacePlacement placement)
    {
        placement = default;
        var monitor = Desktop.MonitorForRect(anchor);
        return Desktop.MonitorArea(monitor, workArea: true) is { } area &&
            TryPlaceIn(window, monitor, area, widthDip, heightDip, out placement, atCorner: false, anchor);
    }

    private static bool TryPlaceIn(Window window, nint monitor, PixelRect area, int widthDip, int heightDip,
        out SurfacePlacement placement, bool atCorner, PixelRect? anchor = null, FloatingTimerPosition? relative = null)
    {
        placement = default;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var scale = Desktop.MonitorForWindow(hwnd) == monitor ? Desktop.OwnedWindowScale(hwnd) : Desktop.MonitorScale(monitor);
        if (Place(scale) is not { } provisional ||
            !MoveIfNeeded(hwnd, provisional) || Desktop.MonitorForWindow(hwnd) != monitor)
        {
            return false;
        }

        // Measure the owned PerMonitorV2 window on the target display, never the invoking app.
        var actualScale = Desktop.OwnedWindowScale(hwnd);
        if (Place(actualScale) is not { } exact ||
            !MoveIfNeeded(hwnd, exact))
        {
            return false;
        }

        var candidate = new SurfacePlacement(monitor, area, exact, UsesWorkArea: true, OwnedScale: actualScale);
        return Confirm(hwnd, candidate, out placement);

        PixelRect? Place(double scale)
        {
            var fitted = MonitorPlacement.Place(area, widthDip, heightDip, scale, atCorner, anchor);
            return relative is { } saved && fitted is { } bounds
                ? MonitorPlacement.PositionWithin(area, bounds, saved.Horizontal, saved.Vertical, FloatingTimerPosition.Extent)
                : fitted;
        }
    }

    internal static bool TryPlaceFloatingTimer(Window window, FloatingTimerPosition? position, int widthDip, int heightDip,
        out SurfacePlacement placement)
    {
        placement = default;
        var monitor = position is { } saved ? Desktop.MonitorNamed(saved.Monitor) : 0;
        if (Desktop.MonitorArea(monitor, workArea: true) is { } savedArea)
        {
            return TryPlaceIn(window, monitor, savedArea, widthDip, heightDip, out placement, atCorner: true, relative: position);
        }
        monitor = Desktop.PrimaryMonitor();
        return Desktop.MonitorArea(monitor, workArea: true) is { } primaryArea &&
            TryPlaceIn(window, monitor, primaryArea, widthDip, heightDip, out placement, atCorner: true);
    }

    internal static bool TryPlaceBreak(Window window, nint source, out SurfacePlacement placement)
    {
        placement = default;
        if (DisplayFor(source, workArea: false) is not { } display)
        {
            return false;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var candidate = new SurfacePlacement(display.Monitor, display.Area, display.Area, UsesWorkArea: false);
        return MoveIfNeeded(hwnd, candidate.Bounds) && Confirm(hwnd, candidate, out placement);
    }

    internal static bool IsCurrent(Window window, SurfacePlacement placement) =>
        IsCurrent(WinRT.Interop.WindowNative.GetWindowHandle(window), placement);

    internal static string DescribePlacement(Window window, SurfacePlacement placement)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var actual = Desktop.WindowBounds(hwnd);
        var actualMonitor = Desktop.MonitorForWindow(hwnd);
        var area = Desktop.MonitorArea(placement.Monitor, placement.UsesWorkArea);
        var scaleMatches = !placement.UsesWorkArea || Desktop.OwnedWindowScale(hwnd) == placement.OwnedScale;
        return $"visible={window.AppWindow.IsVisible} monitorMatches={actualMonitor == placement.Monitor} " +
            $"areaMatches={area == placement.Area} scaleMatches={scaleMatches} " +
            $"expected={FormatBounds(placement.Bounds)} actual={FormatBounds(actual)}";
    }

    private static string FormatBounds(PixelRect? bounds) => bounds is { } rect
        ? $"{rect.X},{rect.Y},{rect.Width},{rect.Height}" : "unavailable";

    internal static bool TryPlaceOrRecover(Window window, nint source, int widthDip, int heightDip, bool previouslyPlaced)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore(activateWindow: false);
            }
            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                if (Desktop.MonitorArea(Desktop.MonitorForWindow(hwnd), workArea: false) is not null)
                {
                    return true;
                }
                presenter.Restore(activateWindow: false);
            }
        }

        if (!previouslyPlaced || Desktop.WindowBounds(hwnd) is not { } existing)
        {
            return TryPlace(window, source, widthDip, heightDip, out _);
        }

        var monitor = Desktop.MonitorForWindow(hwnd);
        var existingArea = Desktop.MonitorArea(monitor, workArea: true);
        if (existingArea?.Contains(existing) == true || HasReachableCaption(hwnd, existing))
        {
            return true;
        }
        var destination = existingArea is { } area ? (Monitor: monitor, Area: area) : DisplayFor(source, workArea: true);
        if (destination is not { } display || MonitorPlacement.Recover(display.Area, existing, center: existingArea is null) is not { } recovered)
        {
            return false;
        }

        if (!MoveIfNeeded(hwnd, recovered))
        {
            return false;
        }
        if (Desktop.WindowBounds(hwnd) != recovered && !Desktop.MoveWithoutActivation(hwnd, recovered))
        {
            return false;
        }
        return Desktop.MonitorArea(display.Monitor, workArea: true) == display.Area &&
            Desktop.WindowBounds(hwnd) is { } actual && display.Area.Contains(actual);
    }

    private static bool HasReachableCaption(nint hwnd, PixelRect bounds)
    {
        var scale = Desktop.OwnedWindowScale(hwnd);
        if (MonitorPlacement.Caption(bounds, scale) is not { } caption)
        {
            return false;
        }
        return Desktop.MonitorArea(Desktop.MonitorForRect(caption), workArea: true) is { } workArea &&
            MonitorPlacement.HasReachableCaption(bounds, workArea, scale);
    }

    private static (nint Monitor, PixelRect Area)? DisplayFor(nint source, bool workArea)
    {
        var preferred = Desktop.MonitorForWindow(source);
        if (Desktop.MonitorArea(preferred, workArea) is { } area)
        {
            return (preferred, area);
        }

        var primary = Desktop.PrimaryMonitor();
        return Desktop.MonitorArea(primary, workArea) is { } fallback ? (primary, fallback) : null;
    }

    private static bool MoveIfNeeded(nint hwnd, PixelRect rect) => Desktop.WindowBounds(hwnd) == rect || Desktop.MoveWithoutActivation(hwnd, rect);

    private static bool IsCurrent(nint hwnd, SurfacePlacement placement) => placement.Monitor != 0 &&
        Desktop.MonitorArea(placement.Monitor, placement.UsesWorkArea) == placement.Area &&
        Desktop.MonitorForWindow(hwnd) == placement.Monitor && Desktop.WindowBounds(hwnd) == placement.Bounds &&
        (!placement.UsesWorkArea || Desktop.OwnedWindowScale(hwnd) == placement.OwnedScale);

    private static bool Confirm(nint hwnd, SurfacePlacement candidate, out SurfacePlacement placement)
    {
        if (IsCurrent(hwnd, candidate))
        {
            placement = candidate;
            return true;
        }

        placement = default;
        return false;
    }

    internal static void ShowAndActivate(Window window)
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
