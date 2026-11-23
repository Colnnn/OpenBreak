using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace OpenBreak;

internal sealed class WindowAppearance : IDisposable
{
    private readonly DispatcherQueue dispatcher;
    private readonly ThemeSettings contrast;
    private readonly UISettings ui = new();
    private readonly List<Action<AppearanceState>> windows = [];
    private AppearancePreferences preferences;
    private AppearanceState current;
    private bool disposed;

    internal WindowAppearance(DispatcherQueue dispatcher, Window anchor, AppearancePreferences preferences)
    {
        this.dispatcher = dispatcher;
        this.preferences = preferences;
        contrast = ThemeSettings.CreateForWindowId(anchor.AppWindow.Id);
        current = ReadState();
        contrast.Changed += SystemChanged;
        ui.AdvancedEffectsEnabledChanged += SystemChanged;
        ui.AnimationsEnabledChanged += SystemChanged;
        ui.ColorValuesChanged += SystemChanged;
        ui.TextScaleFactorChanged += SystemChanged;
    }

    internal void Track(Window window, SurfaceMaterial material)
    {
        var apply = Attach(window, material);
        windows.Add(apply);
        apply(current);
    }

    internal AppearanceState State => current;
    internal event Action<AppearanceState>? Changed;

    internal void Apply(AppearancePreferences settings)
    {
        preferences = settings;
        Refresh();
    }

    public void Dispose()
    {
        disposed = true;
        contrast.Changed -= SystemChanged;
        ui.AdvancedEffectsEnabledChanged -= SystemChanged;
        ui.AnimationsEnabledChanged -= SystemChanged;
        ui.ColorValuesChanged -= SystemChanged;
        ui.TextScaleFactorChanged -= SystemChanged;
        windows.Clear();
    }

    internal static Action<AppearanceState> Attach(Window window, SurfaceMaterial material)
    {
        if (material == SurfaceMaterial.Break)
        {
            return ((BreakWindow)window).ApplyAppearance;
        }

        var frame = material is SurfaceMaterial.Capture or SurfaceMaterial.Warning or SurfaceMaterial.Tray or SurfaceMaterial.FloatingTimer;
        var panel = frame ? (Border)window.Content : new Border
        {
            Style = (Style)Application.Current.Resources["OpenBreakOpaqueBackdropStyle"],
        };
        if (!frame)
        {
            var content = window.Content;
            window.Content = null;
            panel.Child = content;
            window.Content = panel;
        }

        SystemBackdrop? backdrop = material switch
        {
            SurfaceMaterial.Mica when MicaController.IsSupported() => new MicaBackdrop(),
            SurfaceMaterial.Capture or SurfaceMaterial.Tray when DesktopAcrylicController.IsSupported() => new DesktopAcrylicBackdrop(),
            _ => null,
        };

        return state =>
        {
            panel.RequestedTheme = ElementThemeFor(state.Theme);
            var useBackdrop = state.UseMaterials ? backdrop : null;
            if (window.SystemBackdrop != useBackdrop)
            {
                window.SystemBackdrop = useBackdrop;
            }
            if (state.HighContrast)
            {
                panel.Background = SurfaceStyle.Brush(state.WindowColor);
                if (frame)
                {
                    panel.BorderBrush = SurfaceStyle.Brush(state.TextColor);
                }
            }
            else
            {
                if (useBackdrop is null)
                {
                    panel.ClearValue(Border.BackgroundProperty);
                }
                else
                {
                    panel.Background = null;
                }
                panel.ClearValue(Border.BorderBrushProperty);
            }

            switch (window)
            {
                case CaptureWindow capture:
                    capture.ApplyAppearance(state);
                    break;
                case WarningWindow warning:
                    warning.ApplyAppearance(state);
                    break;
                case TrayFlyoutWindow tray:
                    tray.ApplyAppearance(state);
                    break;
                case FloatingTimerWindow floating:
                    floating.ApplyAppearance(state);
                    break;
                case MainWindow main:
                    main.ApplyAppearance(state, panel.RequestedTheme);
                    break;
            }
        };
    }

    internal static ElementTheme ElementThemeFor(AppearanceTheme theme) => theme switch
    {
        AppearanceTheme.Light => ElementTheme.Light,
        AppearanceTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void SystemChanged(object sender, object args) => dispatcher.TryEnqueue(() =>
    {
        if (!disposed)
        {
            Refresh();
        }
    });

    private void Refresh()
    {
        var next = ReadState();
        if (next == current)
        {
            return;
        }

        current = next;
        foreach (var apply in windows)
        {
            apply(current);
        }
        Changed?.Invoke(current);
    }

    private AppearanceState ReadState() => AppearancePolicy.Resolve(preferences, new AppearanceSystem(
        contrast.HighContrast, ui.AdvancedEffectsEnabled, ui.AnimationsEnabled, ui.TextScaleFactor,
        SystemColor(5), SystemColor(8))); // COLOR_WINDOW and COLOR_WINDOWTEXT

    private static uint SystemColor(int index)
    {
        var bgr = GetSysColor(index);
        return ((bgr & 0xff) << 16) | (bgr & 0xff00) | ((bgr >> 16) & 0xff);
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}
