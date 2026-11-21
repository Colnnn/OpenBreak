namespace Afterbreak;

internal enum AppearanceTheme { System, Light, Dark }
internal enum BreakScene { Dusk, Daylight }
internal enum SurfaceMaterial { Mica, Capture, Warning, Break, Tray }

internal sealed record AppearancePreferences(AppearanceTheme Theme, BreakScene Scene)
{
    internal static AppearancePreferences Default { get; } = new(AppearanceTheme.System, BreakScene.Dusk);
    internal bool IsValid => Enum.IsDefined(Theme) && Enum.IsDefined(Scene);
}

internal readonly record struct AppearanceSystem(bool HighContrast, bool Transparency, bool Animations,
    double TextScale, uint WindowColor, uint TextColor);

internal readonly record struct AppearanceState(AppearanceTheme Theme, BreakScene Scene, bool HighContrast,
    bool UseMaterials, bool AnimateScene, double TextScale, uint WindowColor, uint TextColor)
{
    internal bool ShowScene => !HighContrast;
    internal bool AllowsCaptureMotion(bool studyEnabled) => studyEnabled && AnimateScene && !HighContrast;
    internal int ScaleWidth(int width) => Expand(width, 160);
    internal int ScaleHeight(int height) => Expand(height, 215);

    private int Expand(int size, int perMultiplier) => size + (int)Math.Ceiling((TextScale - 1) * perMultiplier);
}

internal static class AppearancePolicy
{
    internal static AppearanceState Resolve(AppearancePreferences preferences, AppearanceSystem system)
    {
        var scale = double.IsFinite(system.TextScale) ? Math.Clamp(system.TextScale, 1, 2.25) : 1;
        return new AppearanceState(system.HighContrast ? AppearanceTheme.System : preferences.Theme, preferences.Scene,
            system.HighContrast, system.Transparency && !system.HighContrast, system.Animations && !system.HighContrast,
            scale, system.WindowColor, system.TextColor);
    }
}
