using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Afterbreak;

internal enum TrayCommand
{
    Capture = 1,
    Preview,
    EndBreak,
    Quit,
    PreviewSampleBoard,
    Tasks,
    Settings,
    TogglePause,
    Snooze,
    Skip,
    CaptureSettings,
    Appearance,
}

internal readonly record struct DesktopArea(int Left, int Top, int Right, int Bottom, double Scale);

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
    private const uint NoRepeat = 0x4000;
    private const uint ForegroundEvent = 3;
    private const string WindowClass = "Afterbreak.Tray";

    private readonly WindowProcedure procedure;
    private readonly WinEventProcedure foregroundProcedure;
    private readonly nint module;
    private readonly uint taskbarCreated;
    private readonly nint window;
    private readonly nint foregroundHook;
    private readonly bool sessionNotifications;
    private readonly uint process = (uint)Environment.ProcessId;
    private nint lastWorkWindow;
    private nint lastOwnWorkWindow;
    private bool iconAdded;
    private bool locked;
    private bool disconnected;
    private bool suspended;

    internal Desktop()
    {
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
    }

    internal CaptureBinding CaptureBinding { get; }
    internal string? DismissLabel { get; set; }
    internal nint BoardHandle { get; set; }
    internal nint SettingsHandle { get; set; }
    internal nint CaptureSettingsHandle { get; set; }
    internal nint AppearanceHandle { get; set; }
    internal bool PreviewAvailable { get; set; } = true;
    internal bool MenuOpen { get; private set; }
    internal bool SnoozeAvailable { get; set; }
    internal bool SkipAvailable { get; set; }
    internal bool ManuallyPaused { get; set; }
    internal string ReminderStatus { get; set; } = "Reminders off";
    internal event Action<TrayCommand, nint>? Command;
    internal event Action? EligibilityChanged;
    internal event Action? DisplaysChanged;

    internal ReminderPause GetSystemPause(bool checkPresentation)
    {
        var pause = (locked || disconnected ? ReminderPause.Locked : ReminderPause.None) |
            (suspended ? ReminderPause.Suspended : ReminderPause.None);
        if (pause == ReminderPause.None && (!HasInputDesktop() ||
            (checkPresentation && (SHQueryUserNotificationState(out var state) != 0 || state != 5))))
        {
            pause |= ReminderPause.Presentation;
        }
        return pause;
    }

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

    internal static DesktopArea? MonitorArea(nint source, bool workArea)
    {
        var monitor = MonitorFromWindow(source, 2);
        var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref information))
        {
            return null;
        }

        var area = workArea ? information.Work : information.Bounds;
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        return new DesktopArea(area.Left, area.Top, area.Right, area.Bottom, scale);
    }

    internal static void AvoidActivation(nint window)
    {
        const int extendedStyle = -20;
        const int noActivateStyle = 0x08000000;
        _ = SetWindowLongPtr(window, extendedStyle, GetWindowLongPtr(window, extendedStyle) | noActivateStyle);
    }

    internal static bool IsForeground(nint window) => GetForegroundWindow() == window;

    internal static void ReturnTo(nint source)
    {
        if (source != 0 && IsWindow(source) && !IsIconic(source) && GetForegroundWindow() != source)
        {
            _ = SetForegroundWindow(source);
        }
    }

    public void Dispose()
    {
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
        if (iconAdded)
        {
            data.Version = 4;
            ShellNotifyIcon(4, ref data);
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
        Id = 1,
        Tip = CaptureBinding.Active is { } active ? $"Afterbreak · {active}" : "Afterbreak · Shortcut unavailable",
        Info = "",
        InfoTitle = "",
    };

    private void ShowMenu()
    {
        var source = WorkWindow();
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        TrayCommand command;
        MenuOpen = true;
        try
        {
            AppendMenu(menu, 0, (nuint)TrayCommand.Tasks, "Open Tasks");
            AppendMenu(menu, 0, (nuint)TrayCommand.Capture,
                CaptureBinding.Active is { } active ? $"Capture task\t{active}" : "Capture task");
            AppendMenu(menu, 0, (nuint)TrayCommand.CaptureSettings, "Capture settings");
            AppendMenu(menu, 0, (nuint)TrayCommand.Appearance, "Appearance");
            var previewFlags = PreviewAvailable && GetSystemPause(checkPresentation: true) == ReminderPause.None ? 0u : 1u; // MF_GRAYED
            AppendMenu(menu, previewFlags, (nuint)TrayCommand.Preview, "Preview a break");
            AppendMenu(menu, previewFlags, (nuint)TrayCommand.PreviewSampleBoard, "Preview sample board");
            if (DismissLabel is { } dismissLabel)
            {
                AppendMenu(menu, 0, (nuint)TrayCommand.EndBreak, dismissLabel);
            }
            AppendMenu(menu, 0x0800, 0, null);
            AppendMenu(menu, 1, 0, ReminderStatus);
            AppendMenu(menu, 0, (nuint)TrayCommand.Settings, "Reminder settings");
            AppendMenu(menu, 0, (nuint)TrayCommand.TogglePause,
                ManuallyPaused ? "Resume after presentation" : "Pause for presentation");
            if (SnoozeAvailable)
            {
                AppendMenu(menu, 0, (nuint)TrayCommand.Snooze, "Snooze 5 min");
            }
            if (SkipAvailable)
            {
                AppendMenu(menu, 0, (nuint)TrayCommand.Skip, "Skip break");
            }
            AppendMenu(menu, 0x0800, 0, null);
            AppendMenu(menu, 0, (nuint)TrayCommand.Quit, "Quit Afterbreak");

            GetCursorPos(out var cursor);
            SetForegroundWindow(window);
            command = (TrayCommand)TrackPopupMenu(menu, 0x0102, cursor.X, cursor.Y, 0, window, 0);
            PostMessage(window, 0, 0, 0);
        }
        finally
        {
            MenuOpen = false;
            DestroyMenu(menu);
        }

        if (GetForegroundWindow() == window || !IsWorkWindow(GetForegroundWindow()))
        {
            ReturnTo(source);
        }
        if (command != 0)
        {
            Command?.Invoke(command, source);
        }
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
            EligibilityChanged?.Invoke();
            return 0;
        }
        if (message == PowerBroadcast && wParam is 4 or 7 or 18)
        {
            // Automatic wake may be unattended. Wait for a user resume or session unlock.
            suspended = wParam != 7;
            EligibilityChanged?.Invoke();
            return 1;
        }
        if (message == TimeChange)
        {
            TimeZoneInfo.ClearCachedData();
        }
        if (message is SettingChange or TimeChange)
        {
            EligibilityChanged?.Invoke();
        }
        if (message == DisplayChange)
        {
            DisplaysChanged?.Invoke();
        }
        if (message == HotkeyMessage)
        {
            var keys = unchecked((uint)lParam);
            if (CaptureBinding.Matches(wParam, keys & 0xffff, keys >> 16, unchecked((uint)GetMessageTime())))
            {
                Command?.Invoke(TrayCommand.Capture, WorkWindow());
            }
            return 0;
        }
        if (message == TrayMessage && ((uint)(nuint)lParam & 0xFFFF) is ContextMenu or SelectNotification or KeyboardSelectNotification)
        {
            ShowMenu();
            return 0;
        }
        if (message == taskbarCreated && taskbarCreated != 0)
        {
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
    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInformation information);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int kind, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
