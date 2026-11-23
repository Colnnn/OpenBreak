using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace OpenBreak;

internal enum ManagementPage { Focus, Tasks, Breaks, Capture, Appearance }

internal sealed class MainWindow : Window
{
    internal readonly record struct FocusBookmark(ManagementPage Page, TasksPage.CardFocus? Card,
        Control? Control, FocusState State, long? TaskFocusGeneration = null)
    {
        internal bool Matches(ManagementPage currentPage, long taskFocusGeneration) =>
            Page == currentPage && (TaskFocusGeneration is not long savedGeneration || savedGeneration == taskFocusGeneration);
    }

    private readonly NavigationView navigation = new()
    {
        IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
        CompactModeThresholdWidth = 700,
        ExpandedModeThresholdWidth = 950,
        OpenPaneLength = 192,
    };
    private readonly Dictionary<ManagementPage, (NavigationViewItem Item, UserControl Content)> pages = [];
    private bool placed;
    private bool quitting;
    private bool tasksNeedRefresh = true;

    internal MainWindow(FocusPage focus, TasksPage tasks, ReminderSettingsPage breaks, CaptureSettingsPage capture,
        AppearanceSettingsPage appearance)
    {
        FocusHome = focus;
        Tasks = tasks;
        Breaks = breaks;
        Capture = capture;
        Appearance = appearance;
        Tasks.CanRestoreFocus = () => HasForeground && CurrentPage == ManagementPage.Tasks;
        Capture.CanRestoreFocus = () => HasForeground && CurrentPage == ManagementPage.Capture;
        Title = "OpenBreak";

        var body = new Grid();
        AddPage(ManagementPage.Focus, "Focus", Symbol.Play, focus);
        AddPage(ManagementPage.Tasks, "Tasks", Symbol.List, tasks);
        navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Settings" });
        AddPage(ManagementPage.Breaks, "Breaks", Symbol.Clock, breaks);
        AddPage(ManagementPage.Capture, "Capture", Symbol.Edit, capture);
        AddPage(ManagementPage.Appearance, "Appearance", Symbol.Setting, appearance);
        navigation.Content = body;
        Content = navigation;
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: ManagementPage page } && page != CurrentPage)
            {
                Navigate(page);
            }
        };
        navigation.SelectedItem = pages[ManagementPage.Focus].Item;

        AppWindow.Closing += (_, args) =>
        {
            if (!quitting)
            {
                args.Cancel = true;
                Tasks.LeavePage();
                AppWindow.Hide();
            }
        };

        void AddPage(ManagementPage page, string label, Symbol icon, UserControl content)
        {
            var item = new NavigationViewItem { Content = label, Icon = new SymbolIcon(icon), Tag = page };
            content.Visibility = page == ManagementPage.Focus ? Visibility.Visible : Visibility.Collapsed;
            pages.Add(page, (item, content));
            navigation.MenuItems.Add(item);
            body.Children.Add(content);
        }
    }

    internal FocusPage FocusHome { get; }
    internal TasksPage Tasks { get; }
    internal ReminderSettingsPage Breaks { get; }
    internal CaptureSettingsPage Capture { get; }
    internal AppearanceSettingsPage Appearance { get; }
    internal ManagementPage CurrentPage { get; private set; }
    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    internal bool HasForeground => AppWindow.IsVisible && Desktop.IsForeground(Handle);
    internal nint GuideSource { get; private set; }
    internal bool GuideHasForeground => !quitting && HasForeground && CurrentPage == ManagementPage.Capture && Capture.GuideVisible;
    internal event Action<ManagementPage>? PageVisited;

    internal bool DefersReminders => AppWindow.IsVisible && (CurrentPage switch
    {
        ManagementPage.Tasks => Tasks.IsEditing,
        ManagementPage.Breaks => Breaks.IsDirty || (HasForeground && Breaks.HasFocusedEditor),
        ManagementPage.Capture => Capture.IsDirty || (HasForeground && Capture.HasFocusedEditor),
        ManagementPage.Appearance => Appearance.IsDirty || (HasForeground && Appearance.HasFocusedEditor),
        _ => false,
    });

    internal void Navigate(ManagementPage page)
    {
        SelectPage(page, Handle);
    }

    internal void OpenGettingStarted()
    {
        Navigate(ManagementPage.Capture);
        if (CurrentPage == ManagementPage.Capture)
        {
            Capture.OpenGuide(focus: true);
        }
    }

    internal bool TryShow(nint source, ManagementPage? page = null, bool activate = true)
    {
        if (!SurfaceWindow.TryPlaceOrRecover(this, source, 1140, 760, placed))
        {
            return false;
        }
        placed = true;
        var requested = page ?? CurrentPage;
        var opening = !AppWindow.IsVisible;
        var origin = opening || page.HasValue || (source != 0 && source != Handle) ? source : GuideSource;
        SelectPage(requested, origin);
        if (activate)
        {
            SurfaceWindow.ShowAndActivate(this);
        }
        else if (!AppWindow.IsVisible)
        {
            AppWindow.Show(activateWindow: false);
        }
        return true;
    }

    private void SelectPage(ManagementPage page, nint source)
    {
        if (CurrentPage == ManagementPage.Tasks && Tasks.HasModalEditor)
        {
            navigation.SelectedItem = pages[CurrentPage].Item;
            return;
        }
        if (page != CurrentPage)
        {
            if (CurrentPage == ManagementPage.Tasks)
            {
                Tasks.LeavePage();
            }
            pages[CurrentPage].Content.Visibility = Visibility.Collapsed;
            CurrentPage = page;
            pages[page].Content.Visibility = Visibility.Visible;
        }
        if (page == ManagementPage.Capture)
        {
            GuideSource = source == 0 ? Handle : source;
        }
        navigation.SelectedItem = pages[page].Item;
        if (page == ManagementPage.Tasks && tasksNeedRefresh)
        {
            Tasks.Refresh();
            tasksNeedRefresh = false;
        }
        PageVisited?.Invoke(page);
    }

    internal void ApplyAppearance(AppearanceState state, ElementTheme theme)
    {
        FocusHome.ApplyAppearance(state);
        Tasks.ApplyAppearance(state, theme);
        Breaks.ApplyAppearance(state);
        Appearance.ApplyAppearance(state);
    }

    internal FocusBookmark? GetFocusBookmark()
    {
        if (!AppWindow.IsVisible || navigation.XamlRoot is not { } root)
        {
            return null;
        }
        var taskGeneration = CurrentPage == ManagementPage.Tasks ? Tasks.FocusGeneration : (long?)null;
        if (taskGeneration.HasValue && Tasks.GetFocusedCard() is { } card)
        {
            return new FocusBookmark(CurrentPage, card, null, card.State, taskGeneration);
        }
        return FocusManager.GetFocusedElement(root) is Control control
            ? new FocusBookmark(CurrentPage, null, control, control.FocusState, taskGeneration) : null;
    }

    internal void NotifyTaskCaptured(FocusBookmark? bookmark)
    {
        var pageMatches = bookmark?.Matches(CurrentPage, Tasks.FocusGeneration) == true;
        if (AppWindow.IsVisible && CurrentPage == ManagementPage.Tasks)
        {
            Tasks.RefreshAfterCapture(pageMatches ? bookmark?.Card : null);
        }
        else
        {
            tasksNeedRefresh = true;
        }
        if (HasForeground && pageMatches && bookmark is { Control: { IsLoaded: true } control } focus)
        {
            control.Focus(focus.State == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic);
        }
    }

    internal nint GuidePreviewTarget => GuideSource != Handle && Desktop.CanReturnTo(GuideSource) ? GuideSource : Handle;

    internal void HandOffGuidePreview(nint target)
    {
        if (target != Handle && GuideHasForeground && Desktop.CanReturnTo(target))
        {
            Desktop.ReturnTo(target);
            AppWindow.Hide();
        }
    }

    internal void CloseForQuit()
    {
        quitting = true;
        Tasks.CloseEditor();
        Close();
    }
}
