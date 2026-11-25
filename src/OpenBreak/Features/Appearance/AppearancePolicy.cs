namespace OpenBreak;

internal enum AppearanceTheme { System, Light, Dark }
internal enum BreakScene { Dusk, Daylight }
internal enum SceneMotion { FollowWindows, Off }
internal enum FloatingTimerStyle { Standard, Compact }
internal enum SurfaceMaterial { Management, Capture, Warning, Break, Tray, FloatingTimer }

internal sealed record AppearancePreferences(AppearanceTheme Theme, BreakScene Scene,
    SceneMotion Motion = SceneMotion.FollowWindows, bool BreakStartSound = false,
    bool TrayCountdown = false, bool FloatingTimer = false, bool FloatingTimerOnTop = false,
    FloatingTimerStyle TimerStyle = FloatingTimerStyle.Standard)
{
    internal static AppearancePreferences Default { get; } = new(AppearanceTheme.System, BreakScene.Dusk);
    internal bool IsValid => Enum.IsDefined(Theme) && Enum.IsDefined(Scene) && Enum.IsDefined(Motion) &&
        Enum.IsDefined(TimerStyle);
}

internal readonly record struct AppearanceSystem(bool HighContrast, bool Transparency, bool Animations,
    double TextScale, uint WindowColor, uint TextColor);

internal readonly record struct AppearanceState(AppearanceTheme Theme, BreakScene Scene, bool HighContrast,
    bool UseMaterials, bool WindowsAnimations, double TextScale, uint WindowColor, uint TextColor,
    SceneMotion Motion = SceneMotion.FollowWindows, FloatingTimerStyle TimerStyle = FloatingTimerStyle.Standard)
{
    internal bool ShowScene => !HighContrast;
    internal bool AnimateScene => WindowsAnimations && !HighContrast && Motion == SceneMotion.FollowWindows;
    internal bool AllowsCaptureMotion(bool studyEnabled) => studyEnabled && WindowsAnimations && !HighContrast;
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
            system.HighContrast, system.Transparency && !system.HighContrast, system.Animations,
            scale, system.WindowColor, system.TextColor, preferences.Motion, preferences.TimerStyle);
    }
}
