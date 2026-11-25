using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OpenBreak;

internal sealed class ManagementChrome
{
    private readonly AppWindow window;
    private readonly FrameworkElement shell;
    private readonly SolidColorBrush paneBackground = new();
    private readonly SolidColorBrush navigationText = new();
    private readonly SolidColorBrush navigationSelectedText = new();
    private readonly SolidColorBrush navigationHeading = new();
    private readonly SolidColorBrush navigationIndicator = new();
    private readonly bool customizeTitleBar = AppWindowTitleBar.IsCustomizationSupported();
    private AppearanceState? appearance;
    private TrayIconDesign? iconDesign;
    private nint icon;

    internal ManagementChrome(Window owner, FrameworkElement shell, NavigationView navigation)
    {
        window = owner.AppWindow;
        this.shell = shell;
        navigation.Resources["NavigationViewDefaultPaneBackground"] = paneBackground;
        navigation.Resources["NavigationViewItemForeground"] = navigationText;
        navigation.Resources["NavigationViewItemForegroundSelected"] = navigationSelectedText;
        navigation.Resources["NavigationViewItemHeaderForeground"] = navigationHeading;
        navigation.Resources["NavigationViewSelectionIndicatorForeground"] = navigationIndicator;
        shell.ActualThemeChanged += OnThemeChanged;
        owner.Closed += (_, _) =>
        {
            shell.ActualThemeChanged -= OnThemeChanged;
            ReleaseIcon();
        };
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        appearance = state;
        Update();
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => Update();

    private void Update()
    {
        if (appearance is not { } state)
        {
            return;
        }

        var dark = shell.ActualTheme == ElementTheme.Dark;
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[dark ? "Default" : "Light"];
        var background = PaletteColor("ShellBackground");
        var text = PaletteColor("Text");
        var secondary = PaletteColor("SecondaryText");
        var accent = PaletteColor("Accent");
        paneBackground.Color = state.HighContrast ? SurfaceStyle.ColorFromRgb(state.WindowColor) : background;
        navigationText.Color = state.HighContrast ? SurfaceStyle.ColorFromRgb(state.TextColor) : text;
        navigationHeading.Color = state.HighContrast ? navigationText.Color : secondary;
        if (state.HighContrast)
        {
            var contrast = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries["HighContrast"];
            navigationSelectedText.Color = ((SolidColorBrush)contrast["OpenBreakManagementOnAccent"]).Color;
            navigationIndicator.Color = navigationSelectedText.Color;
        }
        else
        {
            navigationSelectedText.Color = text;
            navigationIndicator.Color = accent;
        }
        if (customizeTitleBar)
        {
            // Null restores Windows' own caption colors in contrast themes.
            ApplyCaptionColors(state.HighContrast ? null : background,
                state.HighContrast ? null : text,
                state.HighContrast ? null : secondary,
                state.HighContrast ? null : PaletteColor("ShellHover"),
                state.HighContrast ? null : PaletteColor("Stroke"));
        }

        var design = state.HighContrast ? TrayIconDesign.For(null, state) :
            new TrayIconDesign(null, false, 32, Rgb(accent), Rgb(PaletteColor("OnAccent")));
        SetIcon(design with { Size = 32 });

        Color PaletteColor(string role) => ((SolidColorBrush)palette[$"OpenBreakManagement{role}"]).Color;
    }

    private void ApplyCaptionColors(Color? background, Color? text, Color? inactiveText, Color? hover, Color? pressed)
    {
        var caption = window.TitleBar;
        caption.BackgroundColor = background;
        caption.ForegroundColor = text;
        caption.ButtonBackgroundColor = background;
        caption.ButtonForegroundColor = text;
        caption.ButtonHoverBackgroundColor = hover;
        caption.ButtonHoverForegroundColor = text;
        caption.ButtonPressedBackgroundColor = pressed;
        caption.ButtonPressedForegroundColor = text;
        caption.InactiveBackgroundColor = background;
        caption.InactiveForegroundColor = inactiveText;
        caption.ButtonInactiveBackgroundColor = background;
        caption.ButtonInactiveForegroundColor = inactiveText;
    }

    private void SetIcon(TrayIconDesign design)
    {
        if (iconDesign == design)
        {
            return;
        }
        var replacement = TrayCountdownIcon.Create(design);
        if (replacement == 0)
        {
            return;
        }
        try
        {
            window.SetIcon(Win32Interop.GetIconIdFromIcon(replacement));
        }
        catch (COMException)
        {
            TrayCountdownIcon.DestroyIcon(replacement);
            return;
        }
        // Keep the icon alive while the native window references it.
        ReleaseIcon();
        icon = replacement;
        iconDesign = design;
    }

    private void ReleaseIcon()
    {
        if (icon != 0)
        {
            TrayCountdownIcon.DestroyIcon(icon);
            icon = 0;
        }
    }

    private static uint Rgb(Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);
}
