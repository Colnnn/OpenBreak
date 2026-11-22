using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace Afterbreak;

internal sealed class Desktop : IDisposable
{
    private const uint TrayMessage = 0x8001;
    private const uint HotkeyMessage = 0x0312;
    private const uint ContextMenu = 0x007B;
    private const uint DisplayChange = 0x007E;
    private const uint SettingChange = 0x001A;
    private const uint TimeChange = 0x001E;
    private const uint PowerBroadcast = 0x0218;
    private const uint SessionChange = 0x02B1;
    private const uint SelectNotification = 0x0400;
    private const uint KeyboardSelectNotification = 0x0401;
    private const uint LeftButtonUp = 0x0202;
    private const uint RightButtonUp = 0x0205;
    private const uint TrayIconId = 1;
    private const uint NoRepeat = 0x4000;
    private const uint ForegroundEvent = 3;
    private const string WindowClass = "Afterbreak.Tray";
    private const string ShellFullscreenExclusion = "NonRudeHWND";

    private readonly WindowProcedure procedure;
    private readonly WinEventProcedure foregroundProcedure;
    private readonly nint module;
    private readonly uint taskbarCreated;
    private readonly nint window;
    private readonly nint foregroundHook;
    private readonly bool sessionNotifications;
    private readonly DispatcherQueue dispatcher;
    private readonly DisplayAreaWatcher? displayWatcher;
    private readonly TrayMenuLifetime trayMenu = new();
    private readonly uint process = (uint)Environment.ProcessId;
    private nint lastWorkWindow;
    private nint lastOwnWorkWindow;
    private bool iconAdded;
    private bool versionFourIcon;
    private TrayFlyoutWindow? trayFlyout;
    private WindowAppearance? trayAppearance;
    private Func<nint, Action<TrayCommand>>? bindTrayCommand;
    private bool locked;
    private bool disconnected;
    private bool suspended;
    private bool disposed;

    internal Desktop(DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;
        procedure = HandleMessage;
        foregroundProcedure = RecordForeground;
        module = GetModuleHandle(null);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var windowClass = new WindowClassDefinition
        {
            Instance = module,
            Procedure = procedure,
            ClassName = WindowClass,
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        window = CreateWindowEx(0, WindowClass, "Afterbreak tray", 0, 0, 0, 0, 0, 0, 0, module, 0);
        if (window == 0)
        {
            UnregisterClass(WindowClass, module);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var foreground = GetForegroundWindow();
        if (IsWorkWindow(foreground))
        {
            lastWorkWindow = foreground;
        }
        foregroundHook = SetWinEventHook(
            ForegroundEvent, ForegroundEvent, 0, foregroundProcedure, 0, 0, 0x0002);
        sessionNotifications = WTSRegisterSessionNotification(window, 0); // This session only.
        CaptureBinding = new CaptureBinding(
            (id, shortcut) => RegisterHotKey(window, id, shortcut.Modifiers | NoRepeat, shortcut.Key),
            id => UnregisterHotKey(window, id) || Marshal.GetLastPInvokeError() == 1419); // ERROR_HOTKEY_NOT_REGISTERED
        AddIcon();
        displayWatcher = StartDisplayWatcher();
    }

    internal CaptureBinding CaptureBinding { get; }
    internal string? DismissLabel { get; set; }
    internal nint BoardHandle { get; set; }
    internal nint SettingsHandle { get; set; }
    internal nint CaptureSettingsHandle { get; set; }
    internal nint AppearanceHandle { get; set; }
    internal bool PreviewAvailable { get; set; } = true;
    internal bool MenuOpen => trayMenu.Current is not null;
    internal bool TrayFlyoutHasForeground => MenuOpen && trayFlyout?.HasForeground == true;
    internal bool SnoozeAvailable { get; set; }
    internal bool SkipAvailable { get; set; }
    internal bool ManuallyPaused { get; set; }
    internal string ReminderStatus { get; set; } = "Reminders off";
    internal event Action<TrayCommand, nint>? Command;
    internal event Action<string>? EligibilityChanged;
    internal event Action? DisplaysChanged;

    internal void ConfigureTrayFlyout(WindowAppearance appearance, Func<nint, Action<TrayCommand>> bindCommand)
    {
        trayAppearance = appearance;
        bindTrayCommand = bindCommand;
    }

    internal ReminderPause GetSystemPause(bool checkPresentation)
    {
        var pause = (locked || disconnected ? ReminderPause.Locked : ReminderPause.None) |
            (suspended ? ReminderPause.Suspended : ReminderPause.None);
        bool? inputDesktop = null;
        int? shellResult = null;
        int? shellState = null;
        if (pause == ReminderPause.None)
        {
            inputDesktop = HasInputDesktop();
            if (inputDesktop == false)
            {
                pause |= ReminderPause.Presentation;
            }
            else if (checkPresentation)
            {
                shellResult = SHQueryUserNotificationState(out var state);
                shellState = state;
                if (shellResult != 0 || state != 5)
                {
                    pause |= ReminderPause.Presentation;
                }
            }
        }
        LastBreakEligibility = new(pause, inputDesktop, shellResult, shellState);
        return pause;
    }

    internal BreakEligibilitySample LastBreakEligibility { get; private set; }

    private static bool HasInputDesktop()
    {
        var input = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (input == 0)
        {
            return false;
        }
        try
        {
            var name = new StringBuilder(128);
            return GetUserObjectInformation(input, 2, name, name.Capacity * sizeof(char), out _) && name.ToString() == "Default";
        }
        finally
        {
            CloseDesktop(input);
        }
    }

    internal nint WorkWindow()
    {
        var active = GetForegroundWindow();
        ObserveWorkWindow(active);
        if (lastOwnWorkWindow != 0 && IsWindowVisible(lastOwnWorkWindow) && !IsIconic(lastOwnWorkWindow))
        {
            return lastOwnWorkWindow;
        }

        return IsWindow(lastWorkWindow) ? lastWorkWindow : 0;
    }

    internal static nint MonitorForWindow(nint source) => source != 0 && IsWindow(source) ? MonitorFromWindow(source, 0) : 0;

    internal static nint MonitorForRect(PixelRect rect)
    {
        if (!rect.IsValid)
        {
            return 0;
        }
        var bounds = new Rect { Left = rect.X, Top = rect.Y, Right = rect.X + rect.Width, Bottom = rect.Y + rect.Height };
        return MonitorFromRect(ref bounds, 0);
    }

    internal static nint PrimaryMonitor() => MonitorFromPoint(default, 1);

    internal static PixelRect? MonitorArea(nint monitor, bool workArea)
    {
        var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref information))
        {
            return null;
        }

        var area = workArea ? information.Work : information.Bounds;
        return PixelRect.FromEdges(area.Left, area.Top, area.Right, area.Bottom);
    }

    internal static double MonitorScale(nint monitor) => GetScaleFactorForMonitor(monitor, out var scale) == 0 && scale > 0
        ? scale / 100.0 : 1;

    internal static double OwnedWindowScale(nint hwnd) => GetDpiForWindow(hwnd) / 96.0;

    internal static PixelRect? WindowBounds(nint hwnd) => GetWindowRect(hwnd, out var rect)
        ? PixelRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;

    internal static bool MoveWithoutActivation(nint hwnd, PixelRect rect)
    {
        const uint keepZOrderAndFocus = 0x0004 | 0x0010 | 0x0200; // NOZORDER | NOACTIVATE | NOOWNERZORDER
        return rect.IsValid && SetWindowPos(hwnd, 0, rect.X, rect.Y, rect.Width, rect.Height, keepZOrderAndFocus);
    }

    internal static void AvoidActivation(nint window)
    {
        const int extendedStyle = -20;
        const int noActivateStyle = 0x08000000;
        _ = SetWindowLongPtr(window, extendedStyle, GetWindowLongPtr(window, extendedStyle) | noActivateStyle);
    }

    // The Shell must not count our break as a fullscreen app when assessing notifications.
    // This window property leaves other apps and real Windows presentation settings intact.
    internal static bool TryExcludeShellFullscreen(nint window) => SetPropW(window, ShellFullscreenExclusion, 1);

    internal static void ClearShellFullscreenExclusion(nint window) => _ = RemovePropW(window, ShellFullscreenExclusion);

    internal static bool IsForeground(nint window) => GetForegroundWindow() == window;

    internal static void ReturnTo(nint source)
    {
        if (source != 0 && IsWindow(source) && IsWindowVisible(source) && !IsIconic(source) && GetForegroundWindow() != source)
        {
            _ = SetForegroundWindow(source);
        }
    }

    public void Dispose()
    {
        disposed = true;
        DismissTray();
        trayFlyout?.CloseForQuit();
        if (displayWatcher is not null)
        {
            RemoveDisplayHandlers(displayWatcher);
            if (displayWatcher.Status is DisplayAreaWatcherStatus.Started or DisplayAreaWatcherStatus.EnumerationCompleted)
            {
                displayWatcher.Stop();
            }
        }
        if (foregroundHook != 0)
        {
            UnhookWinEvent(foregroundHook);
        }
        CaptureBinding.Dispose();
        if (sessionNotifications)
        {
            WTSUnRegisterSessionNotification(window);
        }
        if (iconAdded)
        {
            var data = IconData();
            ShellNotifyIcon(2, ref data);
        }

        DestroyWindow(window);
        UnregisterClass(WindowClass, module);
    }

    private DisplayAreaWatcher? StartDisplayWatcher()
    {
        DisplayAreaWatcher? watcher = null;
        try
        {
            watcher = DisplayArea.CreateWatcher();
            watcher.Added += OnDisplayAreaChanged;
            watcher.Removed += OnDisplayAreaChanged;
            watcher.Updated += OnDisplayAreaChanged;
            watcher.Start();
            return watcher;
        }
        catch (COMException)
        {
            if (watcher is not null)
            {
                RemoveDisplayHandlers(watcher);
            }
            // WM_DISPLAYCHANGE and retained placement validation are still available.
            return null;
        }
    }

    private void RemoveDisplayHandlers(DisplayAreaWatcher watcher)
    {
        watcher.Added -= OnDisplayAreaChanged;
        watcher.Removed -= OnDisplayAreaChanged;
        watcher.Updated -= OnDisplayAreaChanged;
    }

    private void OnDisplayAreaChanged(DisplayAreaWatcher watcher, DisplayArea area) => QueueDisplayChange();

    private void QueueDisplayChange() => dispatcher.TryEnqueue(() =>
    {
        if (!disposed)
        {
            DismissIfTrayPlacementInvalid();
            DisplaysChanged?.Invoke();
        }
    });

    private bool IsWorkWindow(nint candidate)
    {
        if (candidate == 0 || candidate == GetShellWindow() || !IsWindow(candidate))
        {
            return false;
        }

        GetWindowThreadProcessId(candidate, out var owner);
        var className = new System.Text.StringBuilder(64);
        GetClassName(candidate, className, className.Capacity);
        return owner != process && className.ToString() is not
            ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland");
    }

    private void RecordForeground(nint hook, uint eventId, nint candidate, int objectId, int childId, uint thread, uint time)
        => ObserveWorkWindow(candidate);

    private void ObserveWorkWindow(nint candidate)
    {
        if (candidate != 0 && (candidate == BoardHandle || candidate == SettingsHandle ||
            candidate == CaptureSettingsHandle || candidate == AppearanceHandle))
        {
            lastOwnWorkWindow = candidate;
        }
        else if (IsWorkWindow(candidate))
        {
            lastWorkWindow = candidate;
            lastOwnWorkWindow = 0;
        }
    }

    private void AddIcon()
    {
        var data = IconData();
        data.Flags = 0x87; // Show tip, tip text, icon and callback message.
        data.CallbackMessage = TrayMessage;
        data.Icon = LoadIcon(0, 32516); // Shared system information icon.
        iconAdded = ShellNotifyIcon(0, ref data);
        versionFourIcon = false;
        if (iconAdded)
        {
            data.Version = 4;
            versionFourIcon = ShellNotifyIcon(4, ref data);
        }
    }

    internal void RefreshShortcutTip()
    {
        if (iconAdded)
        {
            var data = IconData();
            data.Flags = 0x84; // NIF_TIP | NIF_SHOWTIP
            ShellNotifyIcon(1, ref data);
        }
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = window,
        Id = TrayIconId,
        Tip = CaptureBinding.Active is { } active ? $"Afterbreak · {active}" : "Afterbreak · Shortcut unavailable",
        Info = "",
        InfoTitle = "",
    };

    private TrayMenuState CurrentTrayMenu() => new(CaptureBinding.Active?.ToString(),
        PreviewAvailable && GetSystemPause(checkPresentation: true) == ReminderPause.None,
        DismissLabel, ReminderStatus, ManuallyPaused, SnoozeAvailable, SkipAvailable);

    internal void RefreshTrayFlyout()
    {
        if (MenuOpen)
        {
            trayFlyout?.UpdateMenu(CurrentTrayMenu());
        }
    }

    private void ShowMenu(uint notification, nuint coordinates)
    {
        if (disposed || trayAppearance is null || bindTrayCommand is null ||
            GetSystemPause(checkPresentation: false) != ReminderPause.None)
        {
            return;
        }
        if (MenuOpen)
        {
            DismissTray();
            return;
        }

        var source = WorkWindow();
        if (TrayAnchor(source, notification, coordinates) is not { } anchor)
        {
            return;
        }
        if (trayFlyout is null)
        {
            trayFlyout = new TrayFlyoutWindow();
            trayAppearance.Track(trayFlyout, SurfaceMaterial.Tray);
        }
        var opening = new TrayOpening(source, bindTrayCommand(source));
        trayFlyout.Prepare(CurrentTrayMenu(), item => SelectTrayAction(opening, item),
            restoreSource => CloseTray(opening, restoreSource));
        if (!trayFlyout.TryPlace(anchor))
        {
            trayFlyout.Hide();
            return;
        }

        trayMenu.Open(opening);
        if (!trayFlyout.TryShow())
        {
            CloseTray(opening, restoreSource: false);
        }
    }

    private PixelRect? TrayAnchor(nint source, uint notification, nuint coordinates)
    {
        var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = TrayIconId };
        if (Shell_NotifyIconGetRect(ref identifier, out var icon) == 0 &&
            PixelRect.FromEdges(icon.Left, icon.Top, icon.Right, icon.Bottom) is { } rectangle && MonitorForRect(rectangle) != 0)
        {
            return rectangle;
        }
        // WM_CONTEXTMENU does not carry version-four coordinates and may come from the keyboard.
        if (versionFourIcon && notification is SelectNotification or KeyboardSelectNotification)
        {
            var point = new PixelRect(unchecked((short)coordinates), unchecked((short)(coordinates >> 16)), 1, 1);
            if (MonitorForRect(point) != 0)
            {
                return point;
            }
        }
        if (!versionFourIcon && notification is LeftButtonUp or RightButtonUp && GetCursorPos(out var cursor))
        {
            var point = new PixelRect(cursor.X, cursor.Y, 1, 1);
            if (MonitorForRect(point) != 0)
            {
                return point;
            }
        }
        var area = MonitorArea(MonitorForWindow(source), workArea: true) ?? MonitorArea(PrimaryMonitor(), workArea: true);
        return area is { } bounds ? new PixelRect(bounds.X + bounds.Width - 1, bounds.Y + bounds.Height - 1, 1, 1) : null;
    }

    private void SelectTrayAction(TrayOpening opening, TrayMenuItem item)
    {
        if (!ReferenceEquals(trayMenu.Current, opening) || !TrayFlyoutHasForeground)
        {
            return;
        }
        if (trayFlyout?.PlacementIsCurrent != true || GetSystemPause(checkPresentation: false) != ReminderPause.None)
        {
            CloseTray(opening, restoreSource: false);
            return;
        }
        if (CurrentTrayMenu().CanInvoke(item) && CloseTray(opening, restoreSource: item.Command != TrayCommand.Quit))
        {
            opening.Dispatch(item.Command);
        }
    }

    private bool CloseTray(TrayOpening opening, bool restoreSource)
    {
        if (!trayMenu.TryClose(opening))
        {
            return false;
        }

        if (restoreSource && trayFlyout?.HasForeground == true)
        {
            ReturnTo(opening.Source);
        }
        trayFlyout?.Hide();
        return true;
    }

    internal void DismissTray()
    {
        if (trayMenu.Current is { } opening)
        {
            CloseTray(opening, restoreSource: false);
        }
    }

    private void DismissIfTrayPlacementInvalid()
    {
        if (trayMenu.Current is { } opening && trayFlyout?.PlacementIsCurrent == false)
        {
            CloseTray(opening, restoreSource: false);
        }
    }

    private void CaptureFromShortcut()
    {
        if (trayMenu.Current is { } opening && TrayFlyoutHasForeground)
        {
            if (CloseTray(opening, restoreSource: trayFlyout?.PlacementIsCurrent == true))
            {
                opening.Dispatch(TrayCommand.Capture);
            }
            return;
        }
        DismissTray();
        Command?.Invoke(TrayCommand.Capture, WorkWindow());
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == SessionChange && wParam is 1 or 2 or 3 or 4 or 7 or 8)
        {
            switch (wParam)
            {
                case 1 or 3: disconnected = false; break;
                case 2 or 4: disconnected = true; break;
                case 7: locked = true; break;
                case 8: locked = false; suspended = false; break;
            }
            if (locked || disconnected)
            {
                DismissTray();
            }
            EligibilityChanged?.Invoke("session-change");
            return 0;
        }
        if (message == PowerBroadcast && wParam is 4 or 7 or 18)
        {
            // Automatic wake may be unattended. Wait for a user resume or session unlock.
            suspended = wParam != 7;
            if (suspended)
            {
                DismissTray();
            }
            EligibilityChanged?.Invoke("power-change");
            return 1;
        }
        if (message == TimeChange)
        {
            TimeZoneInfo.ClearCachedData();
        }
        if (message is SettingChange or TimeChange)
        {
            DismissIfTrayPlacementInvalid();
            EligibilityChanged?.Invoke(message == SettingChange ? "settings-change" : "time-change");
        }
        if (message == DisplayChange)
        {
            QueueDisplayChange();
        }
        if (message == HotkeyMessage)
        {
            var keys = unchecked((uint)lParam);
            if (CaptureBinding.Matches(wParam, keys & 0xffff, keys >> 16, unchecked((uint)GetMessageTime())))
            {
                CaptureFromShortcut();
            }
            return 0;
        }
        if (message == TrayMessage && iconAdded)
        {
            var packed = (uint)(nuint)lParam;
            var notification = packed & 0xFFFF;
            var invoke = versionFourIcon
                ? (packed >> 16) == TrayIconId && notification is ContextMenu or SelectNotification or KeyboardSelectNotification
                : wParam == TrayIconId && notification is LeftButtonUp or RightButtonUp;
            if (invoke)
            {
                ShowMenu(notification, wParam);
            }
            return 0;
        }
        if (message == taskbarCreated && taskbarCreated != 0)
        {
            DismissTray();
            AddIcon();
            return 0;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    private delegate void WinEventProcedure(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassDefinition
    {
        internal uint Style;
        internal WindowProcedure Procedure;
        internal int ExtraClass;
        internal int ExtraWindow;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal string? MenuName;
        internal string ClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        internal int Size;
        internal Rect Bounds;
        internal Rect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClassDefinition windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")]
    private static extern int GetMessageTime();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint instance, nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(nint window);
    [DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int required);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint desktop);
    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data) => Shell_NotifyIcon(message, ref data);
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProcedure callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInformation information);
    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SetPropW(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint RemovePropW(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
