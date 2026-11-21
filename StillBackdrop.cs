using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Afterbreak;

internal static class StillBackdrop
{
    internal static FrameworkElement Create(BreakScene variation)
    {
        var daylight = variation == BreakScene.Daylight;
        uint Shade(uint dusk, uint day) => daylight ? day : dusk;
        // A fixed coastal scene painted in viewport coordinates.
        var coast = new Canvas
        {
            Width = 1440,
            Height = 900,
            Background = Gradient((0, Shade(0x273C4B, 0x4089B2)), (0.42, Shade(0x637C7A, 0x8FC1CF)),
                (0.64, Shade(0x9E9378, 0xE3C58E)), (1, Shade(0x163D46, 0x266E82))),
        };
        var haze = new Ellipse
        {
            Width = 1050,
            Height = 480,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    Stop(0, Shade(0xE2BF89, 0xFCE8AA), 125),
                    Stop(0.75, Shade(0xD2AF82, 0xEACE92), 35),
                    Stop(1, Shade(0xD2AF82, 0xEACE92), 0),
                },
            },
        };
        Canvas.SetLeft(haze, 610);
        Canvas.SetTop(haze, 35);
        coast.Children.Add(haze);

        var sun = new Ellipse { Width = 82, Height = 82, Fill = SurfaceStyle.Brush(Shade(0xE7D5A7, 0xFFF2B5), 210) };
        Canvas.SetLeft(sun, 1110);
        Canvas.SetTop(sun, daylight ? 165 : 245);
        coast.Children.Add(sun);

        coast.Children.Add(Ridge(SurfaceStyle.Brush(Shade(0x526F70, 0x598D89)),
            "M0,517 L0,367 Q87,350 161,374 Q207,384 278,385 Q360,388 436,419 Q531,437 615,466 Q690,488 790,489 Q911,484 1014,494 Q1230,465 1440,455 L1440,530 Z"));
        coast.Children.Add(Ridge(Gradient((0, Shade(0x35545A, 0x416F70)), (1, Shade(0x43757A, 0x598F8B))),
            "M0,620 L0,423 Q90,429 169,418 Q266,411 315,441 Q410,482 484,483 Q568,500 685,512 L720,538 L0,620 Z"));
        coast.Children.Add(Ridge(Gradient((0, Shade(0x6B8278, 0x80B5AA)), (0.24, Shade(0x4F7778, 0x539AAA)),
            (1, Shade(0x214C57, 0x246F86))),
            "M0,578 Q252,550 431,542 Q706,527 910,536 Q1179,522 1440,516 L1440,900 L0,900 Z"));

        for (var index = 0; index < 15; index++)
        {
            var y = 553 + index * index * 1.18;
            var width = 20 + index * 10;
            var center = 1150 + Math.Sin(index * 1.89) * 18;
            coast.Children.Add(new Line
            {
                X1 = center - width / 2,
                X2 = center + width / 2,
                Y1 = y,
                Y2 = y,
                Stroke = SurfaceStyle.Brush(Shade(0xE4D4AB, 0xFBE5AE), (byte)(70 - index * 3)),
                StrokeThickness = 1 + index * 0.1,
            });
        }

        coast.Children.Add(Ridge(Gradient((0, Shade(0x2B5256, 0x4C725C)), (1, Shade(0x102E37, 0x193E42))),
            "M0,900 L0,574 Q89,585 172,619 Q250,649 344,643 Q437,632 522,687 Q570,719 635,741 Q566,782 454,787 Q350,793 280,843 Q215,887 182,900 Z"));
        coast.Children.Add(Ridge(Gradient((0, Shade(0x2F5453, 0x496F58)), (1, Shade(0x132F34, 0x254B42))),
            "M1440,900 L1440,672 Q1346,689 1266,737 Q1197,787 1123,817 Q1036,851 1003,900 Z"));
        coast.Children.Add(Ridge(SurfaceStyle.Brush(Shade(0x102B33, 0x1D423D), 225),
            "M0,776 Q93,773 169,803 Q238,831 305,831 Q280,850 242,870 Q213,885 197,900 L0,900 Z"));

        var scene = new Grid();
        scene.Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = coast });
        scene.Children.Add(new Border { Background = SurfaceStyle.Brush(0x071A22, daylight ? (byte)110 : (byte)75) });
        return scene;
    }

    private static Path Ridge(Brush fill, string silhouette) => new()
    {
        Fill = fill,
        Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), silhouette),
    };

    private static LinearGradientBrush Gradient(params (double Offset, uint Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var stop in stops)
        {
            brush.GradientStops.Add(Stop(stop.Offset, stop.Color));
        }

        return brush;
    }

    private static GradientStop Stop(double offset, uint rgb, byte alpha = 255) => new()
    {
        Offset = offset,
        Color = Windows.UI.Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb),
    };
}
