using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal static class SurfaceStyle
{
    internal const double Caption = 12;
    internal const double Body = 14;
    internal const double StrongBody = 16;
    internal const double SectionTitle = 20;
    internal const double SurfaceTitle = 24;
    internal const double PageTitleSize = 28;

    internal const double TightGap = 4;
    internal const double ControlGap = 8;
    internal const double ContentGap = 12;
    internal const double SectionGap = 16;
    internal const double PageInset = 20;
    internal static CornerRadius PanelCorner => new(8);
    internal static CornerRadius CardCorner => new(4);

    internal static TextBlock Label(string text, double size, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
    };

    internal static TextBlock PageTitle(string text)
    {
        var heading = Label(text, PageTitleSize, FontWeights.SemiBold);
        heading.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        return heading;
    }

    internal static TextBlock Timer(string text, double size)
    {
        var timer = Label(text, size, FontWeights.Light);
        timer.FontFamily = new FontFamily("Segoe UI Variable Display");
        Typography.SetNumeralAlignment(timer, FontNumeralAlignment.Tabular);
        return timer;
    }

    internal static Color ColorFromRgb(uint rgb, byte opacity = 255) =>
        Color.FromArgb(opacity, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    internal static SolidColorBrush Brush(uint rgb, byte opacity = 255) => new(ColorFromRgb(rgb, opacity));

    internal static void ApplyContrast(TextBlock text, AppearanceState state)
    {
        if (state.HighContrast)
        {
            text.Foreground = Brush(state.TextColor);
        }
        else
        {
            text.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    internal static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    internal static TextBlock SecondaryLabel(string text, double size)
    {
        var label = Label(text, size);
        label.Style = (Style)Application.Current.Resources["AfterbreakSecondaryLabelStyle"];
        return label;
    }

    internal static Border Frame(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["AfterbreakSurfaceFrameStyle"],
        Child = child,
    };

    internal static Border Panel(UIElement? child = null) => new()
    {
        Style = (Style)Application.Current.Resources["AfterbreakPanelStyle"],
        Child = child,
        CornerRadius = PanelCorner,
        Padding = new Thickness(ContentGap),
    };
}

internal sealed class ScenePalette
{
    internal SolidColorBrush Background { get; } = SurfaceStyle.Brush(0x172A32);
    internal SolidColorBrush Foreground { get; } = SurfaceStyle.Brush(0xF4F8F2);
    internal SolidColorBrush Secondary { get; } = SurfaceStyle.Brush(0xC7D8CE);
    internal SolidColorBrush Panel { get; } = SurfaceStyle.Brush(0x182E34, 240);
    internal SolidColorBrush Stroke { get; } = SurfaceStyle.Brush(0xABC6BB, 120);

    internal void Apply(AppearanceState state)
    {
        Background.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.WindowColor : 0x172A32);
        Foreground.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.TextColor : 0xF4F8F2);
        Secondary.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.TextColor : 0xC7D8CE);
        Panel.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.WindowColor : 0x182E34,
            state.UseMaterials ? (byte)240 : (byte)255);
        Stroke.Color = SurfaceStyle.ColorFromRgb(state.HighContrast ? state.TextColor : 0xABC6BB,
            state.HighContrast ? (byte)255 : (byte)120);
    }

    internal Border Card(UIElement child) => new()
    {
        Background = Panel,
        BorderBrush = Stroke,
        BorderThickness = new Thickness(1),
        CornerRadius = SurfaceStyle.PanelCorner,
        Padding = new Thickness(SurfaceStyle.SectionGap, SurfaceStyle.ContentGap, SurfaceStyle.SectionGap, SurfaceStyle.ContentGap),
        Child = child,
    };
}
