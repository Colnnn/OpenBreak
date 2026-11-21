namespace Microsoft.UI.Dispatching
{
    internal sealed class DispatcherQueue
    {
        public List<DispatcherQueueTimer> Timers { get; } = [];
        public DispatcherQueueTimer Timer => Timers[0];

        public DispatcherQueueTimer CreateTimer()
        {
            var timer = new DispatcherQueueTimer();
            Timers.Add(timer);
            return timer;
        }
    }

    internal sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool Running { get; private set; }
        public event Action<DispatcherQueueTimer, object>? Tick;

        public void Start() => Running = true;
        public void Stop() => Running = false;

        public void Fire()
        {
            if (Running)
            {
                Tick?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

namespace Afterbreak
{
    internal enum TrayCommand { Capture, Preview, EndBreak, Quit, PreviewSampleBoard, Tasks, Settings, TogglePause, Snooze, Skip, CaptureSettings, Appearance }
    internal enum BreakLayout { Compact, SampleBoard }

    internal sealed class Desktop
    {
        public Desktop() => Current = this;

        public static Desktop Current { get; private set; } = null!;
        public bool Disposed { get; private set; }
        public string? DismissLabel { get; set; }
        public nint BoardHandle { get; set; }
        public nint SettingsHandle { get; set; }
        public nint CaptureSettingsHandle { get; set; }
        public nint AppearanceHandle { get; set; }
        public CaptureBinding CaptureBinding { get; } = new((_, _) => true, _ => true);
        public bool PreviewAvailable { get; set; } = true;
        public bool MenuOpen { get; set; }
        public bool SnoozeAvailable { get; set; }
        public bool SkipAvailable { get; set; }
        public bool ManuallyPaused { get; set; }
        public string ReminderStatus { get; set; } = "";
        public ReminderPause SystemPause { get; set; }
        public Func<ReminderPause>? CheckSystemPause { get; set; }
        public event Action<TrayCommand, nint>? Command;
        public event Action? EligibilityChanged;
        public event Action? DisplaysChanged;

        public ReminderPause GetSystemPause(bool checkPresentation) => CheckSystemPause?.Invoke() ?? SystemPause;
        public nint WorkWindow() => 30;
        public void Send(TrayCommand command, nint source = 10) => Command?.Invoke(command, source);
        public void ChangeEligibility() => EligibilityChanged?.Invoke();
        public void ChangeDisplays() => DisplaysChanged?.Invoke();
        public void RefreshShortcutTip() { }
        public void Dispose()
        {
            CaptureBinding.Dispose();
            Disposed = true;
        }
    }

    internal class TestSurface
    {
        public TestSurface AppWindow => this;
        public bool IsVisible { get; protected set; }
        public bool Closed { get; private set; }

        public void Hide() => IsVisible = false;
        public void Close() => Closed = true;
    }

    internal static class SurfaceWindow
    {
        public static nint Returned { get; private set; }
        public static int Returns { get; private set; }

        public static void HideAndReturn(TestSurface surface, nint hwnd)
        {
            Returned = hwnd;
            Returns++;
            surface.Hide();
        }
    }

    internal sealed class CaptureWindow : TestSurface
    {
        public CaptureWindow(Func<string, bool> save, Action cancel)
        {
            Save = save;
            Cancel = cancel;
            Current = this;
        }

        public static CaptureWindow Current { get; private set; } = null!;
        public Func<string, bool> Save { get; }
        public Action Cancel { get; }
        public int Shows { get; private set; }
        public string Input { get; set; } = "";

        public void Show(nint source)
        {
            Shows++;
            Input = "";
            IsVisible = true;
        }

        public void BringBack(nint source) { }
    }

    internal sealed class WarningWindow : TestSurface
    {
        public WarningWindow(Action snooze, Action skip) => Current = this;

        public static WarningWindow Current { get; private set; } = null!;
        public static int Shows { get; private set; }
        public static int Seconds { get; private set; }
        public static string? Kind { get; private set; }

        public void Show(nint source, string? breakKind = null)
        {
            Shows++;
            Seconds = 10;
            Kind = breakKind;
            IsVisible = true;
        }

        public void Update(int seconds) => Seconds = seconds;
    }

    internal sealed class BreakWindow : TestSurface
    {
        public BreakWindow(Action dismiss) => Current = this;

        public static BreakWindow Current { get; private set; } = null!;
        public static int Shows { get; private set; }
        public static string? Text { get; private set; }
        public static string? Note { get; private set; }
        public static BreakLayout Layout { get; private set; }
        public bool IsLong { get; private set; }
        public int Seconds { get; private set; }
        public bool HasForeground => IsVisible;
        public void StopSceneEntrance() { }

        public void Show(nint source, BreakLayout layout, string? task, string? note = null, bool longBreak = false)
        {
            Shows++;
            Text = task;
            Note = note;
            Layout = layout;
            IsLong = longBreak;
            IsVisible = true;
        }

        public void Update(int seconds) => Seconds = seconds;
    }

    internal sealed class BoardWindow : TestSurface
    {
        internal enum CardAction { Edit, Move }

        internal readonly record struct CardFocus(long TaskId, CardAction Action);

        public BoardWindow(TaskStore store, Action? addTask = null)
        {
            Current = this;
            AddTask = addTask;
        }

        public static BoardWindow Current { get; private set; } = null!;
        public int Refreshes { get; private set; }
        public nint Handle => 300;
        public bool IsEditing { get; set; }
        public Action? AddTask { get; }
        public CardFocus? FocusedCard { get; set; }
        public CardFocus? LastRefreshFocus { get; private set; }

        public void Show(nint source) => IsVisible = true;
        public CardFocus? GetFocusedCard() => FocusedCard;

        public void RefreshAfterCapture(CardFocus? previousFocus)
        {
            Refreshes++;
            LastRefreshFocus = previousFocus;
        }

        public void CloseForQuit() => Close();
    }

    internal sealed class SettingsWindow : TestSurface
    {
        public SettingsWindow(Func<ReminderSettings, bool> save)
        {
            Save = save;
            Current = this;
        }

        public static SettingsWindow Current { get; private set; } = null!;
        public Func<ReminderSettings, bool> Save { get; }
        public nint Handle => 301;
        public ReminderSettings? LoadedSettings { get; private set; }

        public void Show(nint source, ReminderSettings settings, string description, bool storageError)
        {
            LoadedSettings = settings;
            IsVisible = true;
        }

        public void SetStatus(string description) { }
        public void CloseForQuit() => Close();
    }

    internal sealed class CaptureSettingsWindow(Func<CaptureShortcut, string?> apply, Func<bool> dismissGuide) : TestSurface
    {
        public static CaptureSettingsWindow Current { get; private set; } = null!;
        public nint Handle => 302;
        public Func<CaptureShortcut, string?> Apply { get; } = apply;
        public Func<bool> DismissGuide { get; } = dismissGuide;
        public CapturePreferences? Preferences { get; private set; }

        public void Show(nint source, CapturePreferences preferences, string description)
        {
            Current = this;
            Preferences = preferences;
            IsVisible = true;
        }

        public void SetStatus(string description) { }
        public void CloseForQuit() => Close();
    }

    internal sealed class WindowAppearance : IDisposable
    {
        public WindowAppearance(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, TestSurface anchor, AppearancePreferences preferences)
        {
            Current = this;
            Preferences = preferences;
        }

        public static WindowAppearance Current { get; private set; } = null!;
        public AppearancePreferences Preferences { get; private set; }
        public bool Disposed { get; private set; }

        public void Track(TestSurface surface, SurfaceMaterial material) { }
        public void Apply(AppearancePreferences preferences) => Preferences = preferences;
        public void Dispose() => Disposed = true;
    }

    internal sealed class AppearanceSettingsWindow(Func<AppearancePreferences, bool> save) : TestSurface
    {
        public static AppearanceSettingsWindow Current { get; private set; } = null!;
        public nint Handle => 303;
        public Func<AppearancePreferences, bool> Save { get; } = save;
        public AppearancePreferences? Preferences { get; private set; }

        public void Show(nint source, AppearancePreferences preferences, bool loadFailed)
        {
            Current = this;
            Preferences = preferences;
            IsVisible = true;
        }

        public void CloseForQuit() => Close();
    }
}
