using System.Runtime.InteropServices;

namespace Afterbreak;

internal static class TrayCountdownIcon
{
    private static readonly string[] Digits =
    [
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111",
    ];
    private const string Plus = "000010111010000";
    private const string PauseBar = "010010010010010";

    internal static int[] Rasterize(TrayIconDesign design)
    {
        var size = design.Size;
        var pixels = new int[size * size];
        var background = Opaque(design.Background);
        var ink = Opaque(design.Ink);
        Array.Fill(pixels, background);
        pixels[0] = pixels[size - 1] = pixels[size * (size - 1)] = pixels[^1] = 0;
        var inset = Math.Max(1, size / 12);
        var glyphWidth = design.Glyphs.Length * 4 - 1;
        var availableWidth = Math.Min(size - inset * 2, (int)Math.Round(glyphWidth * size / 8.0));
        var left = (size - availableWidth) / 2;
        var top = size / 5 - (design.FinalMinute ? inset : 0);
        var height = size - 2 * (size / 5);
        for (var digit = 0; digit < design.Glyphs.Length; digit++)
        {
            var pattern = design.Glyphs[digit] switch
            {
                '+' => Plus,
                'I' => PauseBar,
                var number => Digits[number - '0'],
            };
            for (var row = 0; row < 5; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    if (pattern[row * 3 + column] == '1')
                    {
                        var x0 = left + (digit * 4 + column) * availableWidth / glyphWidth;
                        var x1 = left + (digit * 4 + column + 1) * availableWidth / glyphWidth;
                        for (var y = top + row * height / 5; y < top + (row + 1) * height / 5; y++)
                        {
                            Array.Fill(pixels, ink, y * size + x0, x1 - x0);
                        }
                    }
                }
            }
        }
        if (design.FinalMinute)
        {
            for (var y = size - 2 * inset; y < size - inset; y++)
            {
                Array.Fill(pixels, ink, y * size + 2 * inset, size - 4 * inset);
            }
        }
        return pixels;
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

    private static int Opaque(uint color) => unchecked((int)(0xff000000 | color));

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
