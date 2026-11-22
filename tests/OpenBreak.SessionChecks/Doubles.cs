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
        public bool IsRunning => Running;
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

namespace OpenBreak
{

    internal sealed class Desktop
    {
        private Func<nint, Action<TrayCommand>>? bindTrayCommand;

        public Desktop(Microsoft.UI.Dispatching.DispatcherQueue dispatcher) => Current = this;

        public static Desktop Current { get; private set; } = null!;
        public bool Disposed { get; private set; }
        public string? DismissLabel { get; set; }
        public nint MainHandle { get; set; }
        public nint FloatingTimerHandle { get; set; }
        public bool? FloatingTimerHidden { get; set; }
        public CountdownDisplay? Countdown { get; private set; }
        public CaptureBinding CaptureBinding { get; } = new((_, _) => true, _ => true);
        public bool PreviewAvailable { get; set; } = true;
        public bool MenuOpen { get; set; }
        public bool TrayMenuHasFocus { get; set; }
        public bool TrayFlyoutHasForeground => MenuOpen && TrayMenuHasFocus;
        public bool LastPresentationCheck { get; private set; }
        public bool SnoozeAvailable { get; set; }
        public int SnoozeSeconds { get; set; } = ReminderSettings.DefaultSnoozeSeconds;
        public bool SkipAvailable { get; set; }
        public bool ManuallyPaused { get; set; }
        public string ReminderStatus { get; set; } = "";
        public ReminderPause SystemPause { get; set; }
        public Func<ReminderPause>? CheckSystemPause { get; set; }
        public event Action<TrayCommand, nint>? Command;
        public event Action<string>? EligibilityChanged;
        public event Action? DisplaysChanged;

        public ReminderPause GetSystemPause(bool checkPresentation)
        {
            LastPresentationCheck = checkPresentation;
            var pause = CheckSystemPause?.Invoke() ?? SystemPause;
            LastBreakEligibility = new(pause, true, checkPresentation ? 0 : null,
                checkPresentation ? pause == ReminderPause.Presentation ? 2 : 5 : null);
            return pause;
        }
        public BreakEligibilitySample LastBreakEligibility { get; private set; }
        public nint WorkSource { get; set; } = 30;
        public nint WorkWindow() => WorkSource;
        public void ConfigureTrayFlyout(WindowAppearance appearance, Func<nint, Action<TrayCommand>> bindCommand) =>
            bindTrayCommand = bindCommand;
        public Action<TrayCommand> OpenTray(nint source)
        {
            MenuOpen = true;
            var dispatch = bindTrayCommand!(source);
            return command =>
            {
                DismissTray();
                dispatch(command);
            };
        }
        public void DismissTray() => MenuOpen = false;
        public void RefreshTrayFlyout() { }
        public void Send(TrayCommand command, nint source = 10) => Command?.Invoke(command, source);
        public void ChangeEligibility(string reason = "session-change") => EligibilityChanged?.Invoke(reason);
        public void ChangeDisplays() => DisplaysChanged?.Invoke();
        public void RefreshShortcutTip() { }
        public void SetCountdown(CountdownDisplay? display, AppearanceState appearance) => Countdown = display;
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
        private readonly Func<string, string?, bool> save;

        public CaptureWindow(Func<string, string?, bool> save, Action cancel, bool captureMotionStudy = false)
        {
            this.save = save;
            CaptureMotionStudy = captureMotionStudy;
            Cancel = cancel;
            Current = this;
        }

        public static CaptureWindow Current { get; private set; } = null!;
        public bool Save(string title, string? note = null) => save(title, note);
        public Action Cancel { get; }
        public int Shows { get; private set; }
        public string Input { get; set; } = "";
        public string NextStep { get; set; } = "";
        public bool CanPlace { get; set; } = true;
        public bool CaptureMotionStudy { get; }
        public bool FocusReady { get; set; } = true;
        public int EntranceStarts { get; private set; }
        public bool EntranceRunning { get; private set; }
        public bool EntranceStoppedWhileVisible { get; private set; }
        public Action? BeforeShow { get; set; }

        public bool TryShow(nint source)
        {
            BeforeShow?.Invoke();
            if (!CanPlace)
            {
                return false;
            }
            Shows++;
            Input = "";
            NextStep = "";
            IsVisible = true;
            return true;
        }

        public bool BringBack(nint source)
        {
            StopCaptureEntrance();
            return CanPlace;
        }

        public void StartCaptureEntrance()
        {
            if (!IsVisible)
            {
                throw new InvalidOperationException("Capture must be shown before its entrance starts.");
            }
            if (CaptureMotionStudy && FocusReady)
            {
                EntranceStarts++;
                EntranceRunning = true;
            }
        }

        public void StopCaptureEntrance()
        {
            if (EntranceRunning)
            {
                EntranceStoppedWhileVisible = IsVisible;
            }
            EntranceRunning = false;
        }
    }

    internal sealed class WarningWindow : TestSurface
    {
        public WarningWindow(Action snooze, Action skip)
        {
            Snooze = snooze;
            Skip = skip;
            Current = this;
        }

        public static WarningWindow Current { get; private set; } = null!;
        public static int Shows { get; private set; }
        public static int Seconds { get; private set; }
        public static string? Kind { get; private set; }
        public bool CanPlace { get; set; } = true;
        public bool PlacementIsCurrent { get; set; } = true;
        public int Attempts { get; private set; }
        public nint LastSource { get; private set; }
        public bool CanShowPrepared { get; set; } = true;
        public Action Snooze { get; }
        public Action Skip { get; }
        public string? SaveError { get; private set; }
        public void ShowSaveError(string message) => SaveError = message;
        public int SnoozeSeconds { get; private set; } = ReminderSettings.DefaultSnoozeSeconds;
        public void SetSnoozeDuration(int seconds) => SnoozeSeconds = seconds;

        public bool TryShow(nint source, string? breakKind = null)
        {
            if (!TryPrepare(source, breakKind))
            {
                return false;
            }
            ShowPrepared();
            return true;
        }

        public bool TryPrepare(nint source, string? breakKind = null)
        {
            Attempts++;
            if (!CanPlace)
            {
                return false;
            }
            LastSource = source;
            Seconds = 10;
            Kind = breakKind;
            SaveError = null;
            PlacementIsCurrent = true;
            return true;
        }

        public void ShowPrepared()
        {
            if (CanShowPrepared)
            {
                Shows++;
                IsVisible = true;
            }
        }

        public void Update(int seconds) => Seconds = seconds;
        public string DescribePlacement() => $"visible={IsVisible} areaMatches={PlacementIsCurrent}";
    }

    internal sealed class BreakWindow : TestSurface
    {
        private readonly Action dismiss;

        public BreakWindow(Action dismiss)
        {
            this.dismiss = dismiss;
            Current = this;
        }

        public void End() => dismiss();

        public static BreakWindow Current { get; private set; } = null!;
        public static int Shows { get; private set; }
        public static string? Text { get; private set; }
        public static string? Note { get; private set; }
        public static BreakLayout Layout { get; private set; }
        public static IReadOnlyList<BoardTask>? SavedTasks { get; private set; }
        public bool IsLong { get; private set; }
        public string? SaveError { get; private set; }
        public void ShowSaveError(string message) => SaveError = message;
        public int Seconds { get; private set; }
        public bool Foreground { get; set; } = true;
        public bool HasForeground => IsVisible && Foreground;
        public bool CanPlace { get; set; } = true;
        public bool CanExcludeShellFullscreen { get; set; } = true;
        public bool ShellFullscreenExcluded { get; private set; }
        public bool RetainedDisplayAvailable { get; set; } = true;
        public bool PlacementIsCurrent => IsVisible && RetainedDisplayAvailable;
        public string DescribePlacement() => $"visible={IsVisible} areaMatches={RetainedDisplayAvailable}";
        public int Attempts { get; private set; }
        public Action? BeforeShow { get; set; }
        public void StopSceneEntrance() { }

        public void SetPreview(BreakView view, bool longBreak = false)
        {
            Layout = view.Layout;
            Text = view.Task?.Title;
            Note = view.Task?.NextStep;
            SavedTasks = view.Tasks;
            IsLong = longBreak;
            SaveError = null;
            Seconds = 10;
        }

        public bool TryShow(nint source)
        {
            BeforeShow?.Invoke();
            Attempts++;
            if (!CanPlace || !CanExcludeShellFullscreen)
            {
                return false;
            }
            Shows++;
            ShellFullscreenExcluded = true;
            IsVisible = true;
            RetainedDisplayAvailable = true;
            return true;
        }

        public void Update(int seconds) => Seconds = seconds;

        public void HideAndReturn(nint source)
        {
            SurfaceWindow.HideAndReturn(this, source);
            ShellFullscreenExcluded = false;
        }

        public void CloseForQuit()
        {
            ShellFullscreenExcluded = false;
            Close();
        }
    }

    internal enum ManagementPage { Focus, Tasks, Breaks, Capture, Appearance }

    internal sealed class MainWindow(FocusPage focus, TasksPage tasks, ReminderSettingsPage breaks,
        CaptureSettingsPage capture, AppearanceSettingsPage appearance) : TestSurface
    {
        internal readonly record struct FocusBookmark(ManagementPage Page, TasksPage.CardFocus? Card);

        public static MainWindow Current { get; private set; } = null!;
        public FocusPage FocusHome { get; } = focus;
        public TasksPage Tasks { get; } = tasks;
        public ReminderSettingsPage Breaks { get; } = breaks;
        public CaptureSettingsPage Capture { get; } = capture;
        public AppearanceSettingsPage Appearance { get; } = appearance;
        public ManagementPage CurrentPage { get; private set; }
        public nint Handle => 300;
        public bool Foreground { get; set; }
        public bool HasForeground => IsVisible && Foreground;
        public bool GuideHasForeground => IsVisible && Capture.Foreground &&
            CurrentPage == ManagementPage.Capture && Capture.GuideVisible;
        public nint GuidePreviewTarget { get; private set; }
        public FocusBookmark? RestoredFocus { get; private set; }
        public int Opens { get; private set; }
        public bool LastActivate { get; private set; }
        public event Action<ManagementPage>? PageVisited;

        public bool DefersReminders => IsVisible && (CurrentPage switch
        {
            ManagementPage.Tasks => Tasks.IsEditing,
            ManagementPage.Breaks => Breaks.IsDirty,
            ManagementPage.Capture => Capture.IsDirty,
            ManagementPage.Appearance => Appearance.IsDirty,
            _ => false,
        });

        public bool TryShow(nint source, ManagementPage? page = null, bool activate = true)
        {
            Current = this;
            Opens++;
            LastActivate = activate;
            if (!IsVisible || page.HasValue || (source != 0 && source != Handle))
            {
                GuidePreviewTarget = source == 0 ? Handle : source;
            }
            IsVisible = true;
            Select(page ?? CurrentPage);
            return true;
        }

        public void Navigate(ManagementPage page)
        {
            GuidePreviewTarget = Handle;
            Select(page);
        }

        public void OpenGettingStarted()
        {
            Navigate(ManagementPage.Capture);
            if (CurrentPage == ManagementPage.Capture)
            {
                Capture.OpenGuide();
            }
        }

        private void Select(ManagementPage page)
        {
            if (CurrentPage == ManagementPage.Tasks && Tasks.HasModalEditor)
            {
                return;
            }
            CurrentPage = page;
            PageVisited?.Invoke(page);
        }

        public FocusBookmark? GetFocusBookmark() => IsVisible
            ? new FocusBookmark(CurrentPage, CurrentPage == ManagementPage.Tasks ? Tasks.FocusedCard : null) : null;

        public void NotifyTaskCaptured(FocusBookmark? bookmark)
        {
            var matched = bookmark is { } saved && saved.Page == CurrentPage;
            RestoredFocus = matched && IsVisible ? bookmark : null;
            if (IsVisible && CurrentPage == ManagementPage.Tasks)
            {
                Tasks.RefreshAfterCapture(matched ? bookmark?.Card : null);
            }
        }

        public void HandOffGuidePreview(nint source)
        {
            if (source != Handle && GuideHasForeground)
            {
                SurfaceWindow.HideAndReturn(this, source);
            }
        }

        public void CloseForQuit() => Close();
    }

    internal sealed class FocusPage(Func<FocusAction, string?> execute, Action<ManagementPage> navigate, Action openGuide)
    {
        public FocusSnapshot Snapshot { get; private set; } = null!;
        public void SetSnapshot(FocusSnapshot snapshot) => Snapshot = snapshot;
        public void SetActionError(string? message) { }
        public string? Execute(FocusAction action) => execute(action);
        public void Navigate(ManagementPage page) => navigate(page);
        public void OpenGettingStarted() => openGuide();
    }

    internal sealed class TasksPage
    {
        internal enum CardAction { Actions, Restore }

        internal readonly record struct CardFocus(long TaskId, CardAction Action);

        public TasksPage(TaskStore store)
        {
            Current = this;
        }

        public static TasksPage Current { get; private set; } = null!;
        public int Refreshes { get; private set; }
        public bool IsEditing { get; set; }
        public bool HasModalEditor { get; set; }
        public CardFocus? FocusedCard { get; set; }
        public CardFocus? LastRefreshFocus { get; private set; }


        public void RefreshAfterCapture(CardFocus? previousFocus)
        {
            Refreshes++;
            LastRefreshFocus = previousFocus;
        }

    }

    internal sealed class ReminderSettingsPage
    {
        public ReminderSettingsPage(Func<ReminderSettings, bool> save)
        {
            this.save = save;
            Current = this;
        }

        private readonly Func<ReminderSettings, bool> save;
        public static ReminderSettingsPage Current { get; private set; } = null!;
        public ReminderSettings LoadedSettings { get; private set; } = ReminderSettings.Default;
        public ReminderSettings Draft { get; set; } = ReminderSettings.Default;
        public bool IsDirty => Draft != LoadedSettings;

        public void SetForm(ReminderSettings settings, bool storageError) => LoadedSettings = Draft = settings;
        public void RebaseBreakDisplay(BreakDisplay display)
        {
            if (Draft.BreakDisplay == LoadedSettings.BreakDisplay)
            {
                Draft = Draft with { BreakDisplay = display };
            }
            LoadedSettings = LoadedSettings with { BreakDisplay = display };
        }

        public void RebaseEnabled(bool enabled)
        {
            if (Draft.Enabled == LoadedSettings.Enabled)
            {
                Draft = Draft with { Enabled = enabled };
            }
            LoadedSettings = LoadedSettings with { Enabled = enabled };
        }

        public bool Save(ReminderSettings settings)
        {
            Draft = settings;
            if (!save(Draft))
            {
                return false;
            }
            LoadedSettings = Draft;
            return true;
        }

        public void SetStatus(string description) { }
    }

    internal sealed class CaptureSettingsPage(Func<CaptureShortcut, string?> apply, Func<bool> dismissGuide,
        Func<BreakDisplay, string?> saveAndPreview, Action<ManagementPage> navigate)
    {
        public static CaptureSettingsPage Current { get; private set; } = null!;
        public CapturePreferences? Preferences { get; private set; }
        public BreakDisplay Display { get; private set; }
        public bool IsDirty { get; set; }
        public bool Foreground { get; set; } = true;
        public bool GuideVisible { get; private set; } = true;
        public void OpenGuide() => GuideVisible = true;
        public void NavigateFromGuide(ManagementPage page) => navigate(page);

        public void SetPreferences(CapturePreferences preferences, string description, BreakDisplay display)
        {
            Current = this;
            Preferences = preferences;
            Display = display;
            GuideVisible = !preferences.GuideDismissed;
        }
        public string? Apply(CaptureShortcut shortcut)
        {
            var result = apply(shortcut);
            if (result is null && Preferences is not null)
            {
                Preferences = Preferences with { Shortcut = shortcut };
            }
            return result;
        }
        public bool DismissGuide()
        {
            if (!dismissGuide() || Preferences is null)
            {
                return false;
            }
            GuideVisible = false;
            Preferences = Preferences with { GuideDismissed = true };
            return true;
        }
        public string? SaveAndPreview(BreakDisplay display) => saveAndPreview(display);
        public void RebaseBreakDisplay(BreakDisplay display) => Display = display;
        public void SetStatus(string description) { }
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
        public AppearanceState State => AppearancePolicy.Resolve(Preferences, new(false, true, true, 1, 0xFFFFFF, 0));
        public bool Disposed { get; private set; }

        public void Track(TestSurface surface, SurfaceMaterial material) { }
        public void Apply(AppearancePreferences preferences) => Preferences = preferences;
        public void Dispose() => Disposed = true;
    }

    internal sealed class FloatingTimerWindow(Action hide, Action<FloatingTimerPosition> save, FloatingTimerPosition? position) : TestSurface
    {
        public static FloatingTimerWindow? Current { get; set; }
        public nint Handle => 1234;
        public CountdownDisplay Display { get; private set; }
        public bool KeepOnTop { get; private set; }
        public FloatingTimerPosition? OriginalPosition { get; } = position;
        public int DisplayChanges { get; private set; }
        public void Present(CountdownDisplay display, bool keepOnTop)
        {
            Display = display;
            KeepOnTop = keepOnTop;
            IsVisible = true;
            Current = this;
        }
        public void DisplayChanged() => DisplayChanges++;
        public void SimulateHide() => hide();
        public void SimulateDrag(FloatingTimerPosition next) => save(next);
        public void CloseForQuit()
        {
            Hide();
            Close();
        }
    }

    internal sealed class AppearanceSettingsPage(Func<AppearancePreferences, bool> save)
    {
        public static AppearanceSettingsPage Current { get; private set; } = null!;
        public AppearancePreferences? Preferences { get; private set; }
        public bool IsDirty { get; set; }

        public void SetPreferences(AppearancePreferences preferences, bool loadFailed)
        {
            Current = this;
            Preferences = preferences;
        }

        public bool Save(AppearancePreferences preferences)
        {
            if (!save(preferences))
            {
                return false;
            }
            SetPreferences(preferences, false);
            IsDirty = false;
            return true;
        }
    }
}
