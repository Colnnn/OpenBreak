using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace OpenBreak;

internal sealed class FocusScene : Canvas
{
    private sealed record Ridge(double Base, double Center, double Rise, double Spread,
        uint LightTop, uint LightBottom, uint DarkTop, uint DarkBottom);

    private const int SceneWidth = 720;
    private const int SceneHeight = 360;
    private const int ContourCount = 24;
    private static readonly Ridge[] Ridges =
    [
        new(214, 583, 127, 160, 0xC4D8AA, 0x9DC1AB, 0x527B60, 0x395C50),
        new(273, 667, 133, 183, 0xA7CBB3, 0x79AF98, 0x416C56, 0x31554A),
        new(312, 528, 102, 157, 0x9DC7B0, 0x55957D, 0x38614E, 0x2B4F45),
        new(367, 680, 116, 132, 0x80B79D, 0x427F6F, 0x315747, 0x25483F),
    ];

    private readonly Canvas artwork = new() { Width = SceneWidth, Height = SceneHeight };
    private readonly Rectangle sunlight = new() { Width = SceneWidth, Height = SceneHeight };
    private readonly Rectangle veil = new() { Width = SceneWidth, Height = SceneHeight };
    private readonly Path[] slopes = new Path[Ridges.Length];
    private readonly Path[] contours = new Path[Ridges.Length];
    private ElementTheme appliedTheme;

    internal FocusScene()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        artwork.Clip = new RectangleGeometry { Rect = new Rect(0, 0, SceneWidth, SceneHeight) };
        artwork.Children.Add(sunlight);
        for (var index = 0; index < Ridges.Length; index++)
        {
            var ridge = Ridges[index];
            slopes[index] = new Path { Data = new PathGeometry { Figures = { Trace(ridge, index, 0, closed: true) } } };
            var lines = new PathGeometry();
            for (var line = 0; line < ContourCount; line++)
            {
                lines.Figures.Add(Trace(ridge, index, line / (ContourCount - 1.0), closed: false));
            }
            contours[index] = new Path { Data = lines, StrokeThickness = 0.8 };
            artwork.Children.Add(slopes[index]);
            artwork.Children.Add(contours[index]);
        }
        artwork.Children.Add(veil);

        // A canvas contributes no desired size. The scene uses the hero's arranged size as the content grows.
        var view = new Viewbox { Child = artwork, Stretch = Stretch.Fill };
        Children.Add(view);
        SizeChanged += (_, args) =>
        {
            view.Width = args.NewSize.Width;
            view.Height = args.NewSize.Height;
        };
        ApplyTheme(ElementTheme.Light);
    }

    internal void ApplyTheme(ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        var effectiveTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        if (effectiveTheme == appliedTheme)
        {
            return;
        }
        appliedTheme = effectiveTheme;
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[dark ? "Default" : "Light"];
        var background = (Color)palette["OpenBreakFocusHeroColor"];
        artwork.Background = Gradient(new Point(140, 0), new Point(670, 360), background,
            SurfaceStyle.ColorFromRgb(dark ? 0x34574Bu : 0xB2DAC2u));

        var glow = dark ? 0x71987Au : 0xF7F9D6u;
        var glowBrush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(548, 62),
            GradientOrigin = new Point(548, 62),
            RadiusX = 178,
            RadiusY = 178,
        };
        glowBrush.GradientStops.Add(new GradientStop { Offset = 0, Color = SurfaceStyle.ColorFromRgb(glow) });
        glowBrush.GradientStops.Add(new GradientStop { Offset = 1, Color = SurfaceStyle.ColorFromRgb(glow, 0) });
        sunlight.Fill = glowBrush;

        var lineColor = dark ? SurfaceStyle.Brush(0xC7F9CC, 56) : SurfaceStyle.Brush(0xEDFFEB, 122);
        for (var index = 0; index < Ridges.Length; index++)
        {
            var ridge = Ridges[index];
            slopes[index].Fill = Gradient(new Point(490, ridge.Base - ridge.Rise), new Point(693, 380),
                SurfaceStyle.ColorFromRgb(dark ? ridge.DarkTop : ridge.LightTop),
                SurfaceStyle.ColorFromRgb(dark ? ridge.DarkBottom : ridge.LightBottom));
            contours[index].Stroke = lineColor;
        }

        var fade = Gradient(new Point(0, 0), new Point(SceneWidth, 0), background,
            Color.FromArgb(0, background.R, background.G, background.B));
        fade.GradientStops.Insert(1, new GradientStop { Offset = 0.35, Color = background });
        fade.GradientStops.Insert(2, new GradientStop { Offset = 0.55, Color = Color.FromArgb(212, background.R, background.G, background.B) });
        fade.GradientStops[3].Offset = 0.86;
        veil.Fill = fade;
    }

    private static LinearGradientBrush Gradient(Point from, Point to, Color start, Color end)
    {
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = from, EndPoint = to };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = start });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = end });
        return brush;
    }

    private static PathFigure Trace(Ridge ridge, int band, double depth, bool closed)
    {
        var points = new PointCollection();
        for (var x = 8; x <= SceneWidth; x += 8)
        {
            points.Add(new Point(x, Elevation(x)));
        }
        if (closed)
        {
            points.Add(new Point(SceneWidth, SceneHeight + 20));
            points.Add(new Point(0, SceneHeight + 20));
        }
        var figure = new PathFigure { StartPoint = new Point(0, Elevation(0)), IsClosed = closed, IsFilled = closed };
        figure.Segments.Add(new PolyLineSegment { Points = points });
        return figure;

        double Elevation(double x)
        {
            var fold = ridge.Rise * Hill(x, ridge.Center + 82 * depth, ridge.Spread + 18 * depth);
            var shoulder = (18 + band * 5) * Hill(x, ridge.Center - 152 + 26 * depth, 69);
            return ridge.Base + 158 * depth - fold - shoulder + 2 * Math.Sin(x / 28 + band + 7 * depth);
        }

        static double Hill(double x, double center, double spread) => Math.Exp(-Math.Pow((x - center) / spread, 2));
    }
}
