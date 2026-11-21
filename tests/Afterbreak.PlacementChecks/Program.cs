using Afterbreak;

static void Check(bool passed, string message)
{
    if (!passed)
    {
        throw new InvalidOperationException(message);
    }
    Console.WriteLine($"PASS {message}");
}

var primary = new PixelRect(0, 0, 1920, 1040);
Check(MonitorPlacement.Place(primary, 460, 165, 1) == new PixelRect(730, 437, 460, 165),
    "100 percent capture centers within the work area");
var left = new PixelRect(-1920, -1040, 1920, 1040);
Check(MonitorPlacement.Place(left, 460, 165, 1.5) == new PixelRect(-1305, -644, 690, 248),
    "150 percent capture preserves negative monitor origins");
var right = new PixelRect(1600, -900, 1600, 860);
Check(MonitorPlacement.Place(right, 340, 138, 2, atCorner: true) == new PixelRect(2480, -356, 680, 276),
    "200 percent warning uses the scaled corner inset and work area");
Check(MonitorPlacement.Place(primary, 400, 200, 1.3) == new PixelRect(700, 390, 520, 260),
    "custom target scale is applied to the original DIP size");

var tiny = new PixelRect(-61, 12, 61, 45);
Check(MonitorPlacement.Place(tiny, 460, 165, 2, atCorner: true) == tiny,
    "tiny work areas clamp size and inset without losing usable pixels");
Check(PixelRect.FromEdges(int.MinValue, 0, int.MaxValue, 10) is null &&
    PixelRect.FromEdges(0, 10, 80, 10) is null && MonitorPlacement.Place(primary, 0, 100, 1) is null &&
    MonitorPlacement.Place(primary, 460, 165, double.NaN) is null && MonitorPlacement.Place(primary, 460, 165, 0) is null,
    "invalid or overflowing bounds, sizes and scales are rejected");

var clipped = new PixelRect(1640, -101, 1000, 660);
var adjacent = new PixelRect(1600, 0, 1600, 860);
Check(!MonitorPlacement.HasReachableCaption(clipped, adjacent, 1) &&
    MonitorPlacement.Recover(adjacent, clipped, center: false) == clipped with { Y = 0 },
    "recover an inaccessible title while preserving usable position and size");
var spanning = new PixelRect(1450, 200, 1000, 660);
Check(MonitorPlacement.HasReachableCaption(spanning, adjacent, 1) && !adjacent.Contains(spanning),
    "a reachable caption keeps an intentionally spanning window usable");
Check(!MonitorPlacement.HasReachableCaption(new PixelRect(3150, -10, 600, 500), adjacent, 1.5) &&
    MonitorPlacement.HasReachableCaption(tiny, tiny, 2), "caption recovery uses a meaningful drag patch adjusted for small windows");
Check(!MonitorPlacement.HasReachableCaption(new PixelRect(-910, 100, 1000, 600), primary, 1),
    "caption buttons alone do not count as a reachable drag area");
Check(MonitorPlacement.Recover(primary, new PixelRect(-3800, -300, 800, 600), center: true) == new PixelRect(560, 220, 800, 600) &&
    MonitorPlacement.Recover(tiny, spanning, center: true) == tiny, "an offscreen window recenters and clamps to the recovered work area");

var trayDisplay = new PixelRect(-1600, -900, 1600, 860);
Check(MonitorPlacement.Place(trayDisplay, 324, 400, 1.5, anchor: new PixelRect(-60, -34, 24, 24)) ==
    new PixelRect(-522, -646, 486, 600), "tray flyout anchors above a bottom taskbar on a scaled negative-origin display");
var topTaskbarArea = new PixelRect(0, 40, 1920, 1040);
Check(MonitorPlacement.Place(topTaskbarArea, 324, 400, 1, anchor: new PixelRect(1840, 8, 24, 24)) ==
    new PixelRect(1540, 40, 324, 400), "tray flyout anchors below a top taskbar");
Check(MonitorPlacement.Place(tiny, 600, 750, 2, anchor: new PixelRect(-3, 45, 1, 1)) == tiny &&
    MonitorPlacement.Place(primary, 324, 400, 1, anchor: default(PixelRect)) is null,
    "tray flyout clamps to tiny work areas and rejects an invalid anchor");
