using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace OpenBreak;

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
        label.Style = (Style)Application.Current.Resources["OpenBreakSecondaryLabelStyle"];
        return label;
    }

    internal static Border Frame(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["OpenBreakSurfaceFrameStyle"],
        Child = child,
    };

    internal static Border Panel(UIElement? child = null) => new()
    {
        Style = (Style)Application.Current.Resources["OpenBreakPanelStyle"],
        Child = child,
        CornerRadius = PanelCorner,
        Padding = new Thickness(ContentGap),
    };

    internal static Style GetStyle(string key) => (Style)Application.Current.Resources[key];
}

internal static class ManagementStyle
{
    internal const double WideInset = 32;
    internal const double NarrowInset = 16;
    internal const double SectionGap = 24;
    internal const double TimerSize = 64;

    internal static Thickness PageMargin(double width) => new(width < 660 ? NarrowInset : WideInset);

    internal static Border Card(UIElement? content = null) => new()
    {
        Style = SurfaceStyle.GetStyle("OpenBreakManagementCardStyle"),
        Child = content,
    };

    internal static Button Button(string text, string automationId, Action click, bool link = false, bool primary = false)
    {
        var button = Button(text, automationId, link, primary);
        button.Click += (_, _) => click();
        return button;
    }

    internal static Button Button(string text, string automationId, bool link = false, bool primary = false)
    {
        var button = new Button
        {
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            Style = SurfaceStyle.GetStyle(primary ? "OpenBreakManagementPrimaryButtonStyle" :
                link ? "OpenBreakManagementLinkButtonStyle" : "OpenBreakManagementButtonStyle"),
        };
        AutomationProperties.SetAutomationId(button, automationId);
        SetButtonText(button, text);
        return button;
    }

    internal static void SetButtonText(Button button, string text)
    {
        var label = (TextBlock)button.Content;
        if (label.Text != text)
        {
            label.Text = text;
            AutomationProperties.SetName(button, text);
        }
    }

    internal static TextBlock Label(string text, double size, bool supporting = false,
        AutomationHeadingLevel heading = AutomationHeadingLevel.None)
    {
        var label = SurfaceStyle.Label(text, size, supporting ? FontWeights.Normal : FontWeights.SemiBold);
        label.Style = SurfaceStyle.GetStyle(supporting ? "OpenBreakManagementSupportingLabelStyle" : "OpenBreakManagementLabelStyle");
        label.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(label, heading);
        return label;
    }

    internal static TextBlock SectionHeading(string text) =>
        Label(text, SurfaceStyle.SectionTitle, heading: AutomationHeadingLevel.Level2);

    internal static TextBlock BodyText(string text) => Label(text, SurfaceStyle.Body, supporting: true);

    internal static void ApplyContrast(UIElement element, AppearanceState state)
    {
        switch (element)
        {
            case Border border:
                if (ReferenceEquals(border.Style, SurfaceStyle.GetStyle("OpenBreakManagementCardStyle")) ||
                    ReferenceEquals(border.Style, SurfaceStyle.GetStyle("OpenBreakTaskLaneStyle")))
                {
                    if (state.HighContrast)
                    {
                        border.Background = SurfaceStyle.Brush(state.WindowColor);
                        border.BorderBrush = SurfaceStyle.Brush(state.TextColor);
                    }
                    else
                    {
                        border.ClearValue(Border.BackgroundProperty);
                        border.ClearValue(Border.BorderBrushProperty);
                    }
                }
                if (border.Child is { } child)
                {
                    ApplyContrast(child, state);
                }
                break;
            case Panel panel:
                foreach (var item in panel.Children)
                {
                    ApplyContrast(item, state);
                }
                break;
            case ScrollViewer { Content: UIElement content }:
                ApplyContrast(content, state);
                break;
            case TextBlock label:
                SurfaceStyle.ApplyContrast(label, state);
                break;
        }
    }
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
