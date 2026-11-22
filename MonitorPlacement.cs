namespace Afterbreak;

internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    internal bool IsValid => Width > 0 && Height > 0 && (long)X + Width <= int.MaxValue && (long)Y + Height <= int.MaxValue;

    internal static PixelRect? FromEdges(int left, int top, int right, int bottom)
    {
        var width = (long)right - left;
        var height = (long)bottom - top;
        return width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue
            ? new PixelRect(left, top, (int)width, (int)height) : null;
    }

    internal bool Contains(PixelRect other) => IsValid && other.IsValid && other.X >= X && other.Y >= Y &&
        (long)other.X + other.Width <= (long)X + Width && (long)other.Y + other.Height <= (long)Y + Height;
}

internal readonly record struct SurfacePlacement(nint Monitor, PixelRect Area, PixelRect Bounds, bool UsesWorkArea, double OwnedScale = 0);

internal static class MonitorPlacement
{
    internal static PixelRect? Place(PixelRect area, int widthDip, int heightDip, double scale, bool atCorner = false, PixelRect? anchor = null)
    {
        if (!area.IsValid || widthDip <= 0 || heightDip <= 0 || !double.IsFinite(scale) || scale <= 0)
        {
            return null;
        }

        var width = (int)Math.Clamp(Math.Round(widthDip * scale), 1, area.Width);
        var height = (int)Math.Clamp(Math.Round(heightDip * scale), 1, area.Height);
        if (anchor is { } trayIcon)
        {
            if (!trayIcon.IsValid)
            {
                return null;
            }
            var gap = Pixels(8, scale, area.Height);
            var above = (long)trayIcon.Y - height - gap;
            var below = (long)trayIcon.Y + trayIcon.Height + gap;
            var xAtIcon = (long)trayIcon.X + trayIcon.Width - width;
            return new PixelRect((int)Math.Clamp(xAtIcon, area.X, (long)area.X + area.Width - width),
                (int)Math.Clamp(above >= area.Y ? above : below, area.Y, (long)area.Y + area.Height - height), width, height);
        }
        var inset = Math.Round(20 * scale);
        var spaceX = area.Width - width;
        var spaceY = area.Height - height;
        var x = atCorner ? (int)Math.Max(0, spaceX - inset) : spaceX / 2;
        var y = atCorner ? (int)Math.Max(0, spaceY - inset) : spaceY / 2;
        return new PixelRect(area.X + x, area.Y + y, width, height);
    }

    internal static PixelRect? Recover(PixelRect area, PixelRect window, bool center)
    {
        if (!area.IsValid || !window.IsValid)
        {
            return null;
        }

        var width = Math.Min(window.Width, area.Width);
        var height = Math.Min(window.Height, area.Height);
        var x = center ? area.X + (area.Width - width) / 2 : Math.Clamp(window.X, area.X, area.X + (area.Width - width));
        var y = center ? area.Y + (area.Height - height) / 2 : Math.Clamp(window.Y, area.Y, area.Y + (area.Height - height));
        return new PixelRect(x, y, width, height);
    }

    internal static PixelRect PositionWithin(PixelRect area, PixelRect fitted, int horizontal, int vertical, int extent) =>
        fitted with
        {
            X = area.X + (int)Math.Round((area.Width - fitted.Width) * ((double)horizontal / extent)),
            Y = area.Y + (int)Math.Round((area.Height - fitted.Height) * ((double)vertical / extent)),
        };

    internal static int RelativeOffset(int coordinate, int edge, int travel, int extent) =>
        travel <= 0 ? 0 : (int)Math.Round(Math.Clamp(((double)coordinate - edge) / travel, 0, 1) * extent);

    internal static PixelRect? Caption(PixelRect window, double scale)
    {
        if (!window.IsValid || !double.IsFinite(scale) || scale <= 0)
        {
            return null;
        }

        // A sliver containing only Minimize/Maximize/Close cannot be dragged back.
        var buttons = Math.Min(window.Width - 1, Pixels(144, scale, Math.Max(1, window.Width / 2)));
        return window with { Width = window.Width - buttons, Height = Pixels(32, scale, window.Height) };
    }

    internal static bool HasReachableCaption(PixelRect window, PixelRect area, double scale)
    {
        if (!area.IsValid || Caption(window, scale) is not { } caption)
        {
            return false;
        }
        var width = Math.Min((long)caption.X + caption.Width, (long)area.X + area.Width) - Math.Max(caption.X, area.X);
        var height = Math.Min((long)caption.Y + caption.Height, (long)area.Y + area.Height) - Math.Max(caption.Y, area.Y);
        return width >= Pixels(80, scale, caption.Width) && height >= Pixels(16, scale, caption.Height);
    }

    private static int Pixels(int dip, double scale, int limit) => (int)Math.Clamp(Math.Ceiling(dip * scale), 1, limit);
}
