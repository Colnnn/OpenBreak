using System.Runtime.InteropServices;

namespace OpenBreak;

internal static class TrayCountdownIcon
{
    // Rounded monoline figures in a 16-unit icon. Keep their counters open at 16 px.
    private static readonly Point[][] Digits =
    [
        [new(-.8, -4), new(.8, -4), new(1.7, -3), new(1.7, 3), new(.8, 4), new(-.8, 4), new(-1.7, 3), new(-1.7, -3), new(-.8, -4)],
        [new(-1.2, -2.5), new(.2, -4), new(.2, 4)],
        [new(-1.6, -3), new(-.8, -4), new(.8, -4), new(1.7, -3), new(1.7, -1.5), new(-1.7, 2.2), new(-1.7, 4), new(1.7, 4)],
        [new(-1.5, -4), new(.8, -4), new(1.7, -3), new(1.7, -1), new(.8, 0), new(-.5, 0),
            new(.8, 0), new(1.7, 1), new(1.7, 3), new(.8, 4), new(-1.5, 4)],
        [new(-.4, -4), new(-1.7, .8), new(1.7, .8), new(1.7, -4), new(1.7, 4)],
        [new(1.6, -4), new(-1.5, -4), new(-1.5, -.2), new(.7, -.2), new(1.7, .8), new(1.7, 3), new(.7, 4), new(-1.5, 4)],
        [new(1.3, -4), new(-.5, -4), new(-1.7, -2.4), new(-1.7, 3), new(-.8, 4), new(.8, 4),
            new(1.7, 3), new(1.7, .7), new(.8, -.2), new(-1.7, -.2)],
        [new(-1.7, -4), new(1.7, -4), new(1.7, -3), new(-.7, 4)],
        [new(.8, 0), new(1.6, -1), new(1.6, -3), new(.7, -4), new(-.7, -4), new(-1.6, -3), new(-1.6, -1), new(-.8, 0),
            new(.8, 0), new(1.7, 1), new(1.7, 3), new(.8, 4), new(-.8, 4), new(-1.7, 3), new(-1.7, 1), new(-.8, 0)],
        [new(-1.3, 4), new(.5, 4), new(1.7, 2.4), new(1.7, -3), new(.8, -4), new(-.8, -4),
            new(-1.7, -3), new(-1.7, -.7), new(-.8, .2), new(1.7, .2)],
    ];

    internal static int[] Rasterize(TrayIconDesign design)
    {
        var size = design.Size;
        var pixels = new int[size * size];
        var pixelWidth = 16.0 / size;
        var strokes = MakeStrokes(design);
        var strokeRadius = design.Glyphs switch
        {
            null => .86,
            "II" => .90,
            { Length: 3 } => .63,
            _ => .83,
        };
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                var x = (column + .5) * pixelWidth - 8;
                var y = (row + .5) * pixelWidth - 8;
                var cx = Math.Max(0, Math.Abs(x) - 4.15);
                var cy = Math.Max(0, Math.Abs(y) - 3.9);
                var alpha = Coverage(Math.Sqrt(cx * cx + cy * cy) - 3.5);
                if (alpha == 0)
                {
                    continue;
                }
                var distance = double.PositiveInfinity;
                foreach (var stroke in strokes)
                {
                    distance = Math.Min(distance, stroke.SquaredDistance(x, y));
                }
                var ink = Coverage(Math.Sqrt(distance) - strokeRadius);
                pixels[row * size + column] = Pixel(design.Background, design.Ink, alpha, ink);
            }
        }
        return pixels;

        double Coverage(double distance) => Math.Clamp(.5 - distance / pixelWidth, 0, 1);
    }

    private static List<Stroke> MakeStrokes(TrayIconDesign design)
    {
        List<Stroke> strokes = [];
        if (design.Glyphs is not { } glyphs)
        {
            // An open circular ring frames the pause symbol.
            const int steps = 20;
            var previous = RingPoint(0);
            for (var step = 1; step <= steps; step++)
            {
                var next = RingPoint(step);
                strokes.Add(new Stroke(previous, next));
                previous = next;
            }
            Line(-1.65, -2.3, -1.65, 2.3);
            Line(1.65, -2.3, 1.65, 2.3);
            return strokes;

            static Point RingPoint(int step)
            {
                var angle = (step * 310.0 / steps - 15) * Math.PI / 180;
                return new Point(5.2 * Math.Cos(angle), 5.2 * Math.Sin(angle));
            }
        }
        var y = design.FinalMinute ? -1.1 : 0;
        var stepWidth = glyphs.Length == 3 ? 4.75 : glyphs == "II" ? 3.4 : 5.4;
        var horizontalScale = glyphs.Length == 3 ? .8 : 1;
        for (var index = 0; index < glyphs.Length; index++)
        {
            var x = (index - (glyphs.Length - 1) * .5) * stepWidth;
            switch (glyphs[index])
            {
                case 'I':
                    Line(x, -3.6, x, 3.6);
                    break;
                case '+':
                    Line(x - 1.25, y, x + 1.25, y);
                    Line(x, y - 1.6, x, y + 1.6);
                    break;
                case var digit when char.IsAsciiDigit(digit):
                    var path = Digits[digit - '0'];
                    for (var segment = 1; segment < path.Length; segment++)
                    {
                        var a = path[segment - 1];
                        var b = path[segment];
                        Line(x + a.X * horizontalScale, y + a.Y, x + b.X * horizontalScale, y + b.Y);
                    }
                    if (digit == '1')
                    {
                        Line(x - 1.2 * horizontalScale, y + 4, x + 1.5 * horizontalScale, y + 4);
                    }
                    break;
            }
        }
        if (design.FinalMinute)
        {
            Line(-3.0, 5.3, 3.0, 5.3);
        }
        return strokes;

        void Line(double x0, double y0, double x1, double y1) =>
            strokes.Add(new Stroke(new Point(x0, y0), new Point(x1, y1)));
    }

    private static int Pixel(uint background, uint ink, double alpha, double coverage)
    {
        // Windows expects the DIB's BGRA color channels to be premultiplied by alpha.
        var a = (int)Math.Round(alpha * 255);
        return (a << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);

        int Channel(int shift)
        {
            var from = (background >> shift) & 0xff;
            var to = (ink >> shift) & 0xff;
            return (int)Math.Round(alpha * (from * (1 - coverage) + to * coverage));
        }
    }

    private readonly record struct Point(double X, double Y);

    private readonly struct Stroke(Point from, Point to)
    {
        private readonly double dx = to.X - from.X;
        private readonly double dy = to.Y - from.Y;

        internal double SquaredDistance(double x, double y)
        {
            var along = Math.Clamp(((x - from.X) * dx + (y - from.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            var offsetX = x - from.X - along * dx;
            var offsetY = y - from.Y - along * dy;
            return offsetX * offsetX + offsetY * offsetY;
        }
    }

    internal static nint Create(TrayIconDesign design)
    {
        var bitmap = new BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<BitmapInfo>(),
            Width = design.Size,
            Height = -design.Size,
            Planes = 1,
            Bits = 32,
        };
        var color = CreateDIBSection(0, ref bitmap, 0, out var buffer, 0, 0);
        if (color == 0)
        {
            return 0;
        }
        nint mask = 0;
        try
        {
            var pixels = Rasterize(design);
            Marshal.Copy(pixels, 0, buffer, pixels.Length);
            var maskStride = ((design.Size + 15) / 16) * 2;
            mask = CreateBitmap(design.Size, design.Size, 1, 1, new byte[maskStride * design.Size]);
            return mask == 0 ? 0 : CreateIconIndirect(new IconInfo { IsIcon = true, Mask = mask, Color = color });
        }
        finally
        {
            if (mask != 0)
            {
                DeleteObject(mask);
            }
            DeleteObject(color);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, Bits;
        internal uint Compression, ImageSize;
        internal int XPixelsPerMeter, YPixelsPerMeter;
        internal uint ColorsUsed, ImportantColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] internal bool IsIcon;
        internal uint XHotspot, YHotspot;
        internal nint Mask, Color;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bits, byte[] pixels);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(in IconInfo info);
    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(nint icon);
}
