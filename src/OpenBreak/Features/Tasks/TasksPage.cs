using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace OpenBreak;

internal sealed class TasksPage : UserControl
{
    internal enum CardAction { Actions, Restore, Delete }

    internal readonly record struct CardFocus(long TaskId, CardAction Action, FocusState State);
    private readonly record struct ActionOrigin(CardFocus Card, long Generation);

    private const string DragFormat = "OpenBreak.BoardTask";
    private static readonly (TaskStatus Status, string Label)[] Columns =
    [
        (TaskStatus.ToDo, "To do"),
        (TaskStatus.InProgress, "In progress"),
        (TaskStatus.Done, "Done"),
    ];

    private readonly TaskStore store;
    private readonly Grid root;
    private readonly Grid columns;
    private readonly Grid header;
    private readonly ActionRow headerActions;
    private readonly TextBlock heading;
    private readonly Button addButton;
    private readonly Button archiveButton;
    private readonly ScrollViewer scroll;
    private readonly InfoBar error;
    private readonly Button retry;
    private readonly Grid inlineEditor;
    private readonly TextBox draftTitle = new() { Header = "Title", MaxLength = TaskStore.TitleLimit };
    private readonly TextBox draftNextStep = new() { Header = "Next step (optional)", MaxLength = TaskStore.NextStepLimit };
    private readonly InfoBar draftError = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly Dictionary<(long TaskId, CardAction Action), Control> taskActions = [];
    private readonly Border[] lanes = new Border[Columns.Length];
    private bool archived;
    private bool draftOpen;
    private bool lanesStacked;
    private long? draggedTask;
    private double textScale = 1;
    private AppearanceState? appearance;
    private ElementTheme dialogTheme;
    private ContentDialog? activeDialog;
    private MenuFlyout? openMenu;
    private CardFocus? menuOwner;
    private int menusAwaitingClose;
    private long focusGeneration;
    private bool refreshPending;
    private ActionOrigin? pendingFocus;
    private Action? retryChange;

    internal TasksPage(TaskStore store)
    {
        this.store = store;

        heading = ManagementStyle.Label("Tasks", SurfaceStyle.PageTitleSize, heading: AutomationHeadingLevel.Level1);
        heading.VerticalAlignment = VerticalAlignment.Center;
        addButton = ManagementStyle.Button("Add task", "TasksAdd", () => OpenInlineEditor(focus: true), primary: true);
        archiveButton = ManagementStyle.Button("Archive", "TasksArchive", () => ShowArchive(!archived));

        headerActions = new ActionRow { HorizontalAlignment = HorizontalAlignment.Right };
        headerActions.Children.Add(archiveButton);
        headerActions.Children.Add(addButton);
        header = new Grid { ColumnSpacing = SurfaceStyle.SectionGap, RowSpacing = SurfaceStyle.ControlGap };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.Children.Add(heading);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        retry = ManagementStyle.Button("Retry", "TasksRetry", () => retryChange?.Invoke());
        error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, Title = "Task storage" };

        AutomationProperties.SetAutomationId(draftTitle, "TasksDraftTitle");
        AutomationProperties.SetAutomationId(draftNextStep, "TasksDraftNextStep");
        var save = ManagementStyle.Button("Save task", "TasksSave", () => SaveInlineDraft(focus: true), primary: true);
        var cancel = ManagementStyle.Button("Cancel", "TasksCancel", () => CancelInlineDraft(focus: true));
        var editorActions = new ActionRow();
        editorActions.Children.Add(save);
        editorActions.Children.Add(cancel);
        var editor = SettingsLayout.Group("New task", draftTitle, draftNextStep, draftError, editorActions);
        inlineEditor = new Grid { Visibility = Visibility.Collapsed };
        inlineEditor.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 640 });
        inlineEditor.Children.Add(ManagementStyle.Card(editor));
        draftTitle.KeyDown += SaveOnEnter;
        draftNextStep.KeyDown += SaveOnEnter;
        inlineEditor.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape && inlineEditor.Visibility == Visibility.Visible)
            {
                args.Handled = true;
                CancelInlineDraft(focus: true);
            }
        };

        columns = new Grid { ColumnSpacing = SurfaceStyle.SectionGap };
        for (var index = 0; index < Columns.Length; index++)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lanes[index] = new Border
            {
                Style = SurfaceStyle.GetStyle("OpenBreakTaskLaneStyle"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            AutomationProperties.SetAutomationId(lanes[index], $"TasksLane{Columns[index].Status}");
            Grid.SetColumn(lanes[index], index);
            columns.Children.Add(lanes[index]);
        }
        var board = new StackPanel { Spacing = ManagementStyle.SectionGap };
        board.Children.Add(inlineEditor);
        board.Children.Add(columns);
        scroll = new ScrollViewer
        {
            Content = board,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root = new Grid { Padding = ManagementStyle.PageMargin(0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        header.Margin = new Thickness(0, 0, 0, ManagementStyle.SectionGap);
        root.Children.Add(header);
        Grid.SetRow(error, 1);
        root.Children.Add(error);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Content = root;
        SizeChanged += (_, args) => LayoutBoard(args.NewSize.Width);
        LayoutBoard(0);
    }

    internal bool HasModalEditor => activeDialog is not null;
    internal bool IsEditing => HasModalEditor || (!archived && draftOpen);
    internal long FocusGeneration => focusGeneration;
    internal Func<bool>? CanRestoreFocus { private get; set; }

    internal void ApplyAppearance(AppearanceState state, ElementTheme theme)
    {
        dialogTheme = theme;
        if (activeDialog is not null)
        {
            activeDialog.RequestedTheme = theme;
        }
        appearance = state;
        textScale = state.TextScale;
        LayoutBoard(ActualWidth);
        ManagementStyle.ApplyContrast(root, state);
    }

    private void LayoutBoard(double width)
    {
        root.Padding = ManagementStyle.PageMargin(width);
        var contentWidth = width - 2 * root.Padding.Left;
        var stack = contentWidth < 350 * textScale;
        Grid.SetColumn(headerActions, stack ? 0 : 1);
        Grid.SetColumnSpan(headerActions, stack ? 2 : 1);
        Grid.SetRow(headerActions, stack ? 1 : 0);
        headerActions.HorizontalAlignment = stack ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Grid.SetColumnSpan(heading, stack ? 2 : 1);
        LayoutLanes(contentWidth < 690 * textScale);
    }

    internal void CloseEditor() => activeDialog?.Hide();

    internal void LeavePage()
    {
        focusGeneration++;
        openMenu?.Hide();
    }

    internal void OpenInlineEditor(bool focus = false)
    {
        if (archived || HasModalEditor)
        {
            return;
        }

        var generation = ++focusGeneration;
        draftOpen = true;
        inlineEditor.Visibility = Visibility.Visible;
        if (focus)
        {
            FocusWhenReady(draftTitle, FocusState.Keyboard, () =>
                generation == focusGeneration && inlineEditor.Visibility == Visibility.Visible &&
                CanRestoreFocus?.Invoke() == true);
        }
    }

    internal void SaveInlineDraft(bool focus = false)
    {
        if (inlineEditor.Visibility != Visibility.Visible)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draftTitle.Text))
        {
            ShowDraftError("Enter a task title.");
            return;
        }

        try
        {
            store.Add(draftTitle.Text, draftNextStep.Text);
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            ShowDraftError("Couldn’t save the task. Your draft is still here. Try Save task again.");
            return;
        }

        CancelInlineDraft(focus);
        Refresh();
    }

    internal void CancelInlineDraft(bool focus = false)
    {
        var generation = ++focusGeneration;
        draftOpen = false;
        draftTitle.Text = "";
        draftNextStep.Text = "";
        draftError.IsOpen = false;
        inlineEditor.Visibility = Visibility.Collapsed;
        if (focus)
        {
            FocusWhenReady(addButton, FocusState.Keyboard, () =>
                generation == focusGeneration && !archived && !IsEditing && CanRestoreFocus?.Invoke() == true);
        }
    }

    private void ShowDraftError(string message)
    {
        draftError.Message = message;
        draftError.IsOpen = true;
    }

    private void SaveOnEnter(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            SaveInlineDraft(focus: true);
        }
    }

    internal void ShowArchive(bool show)
    {
        if (show == archived)
        {
            return;
        }
        focusGeneration++;
        archived = show;
        heading.Text = archived ? "Archive" : "Tasks";
        ManagementStyle.SetButtonText(archiveButton, archived ? "Back to board" : "Archive");
        addButton.Visibility = archived ? Visibility.Collapsed : Visibility.Visible;
        inlineEditor.Visibility = !archived && draftOpen ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
        openMenu?.Hide();
        scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    internal CardFocus? GetFocusedCard()
    {
        if (menuOwner is { } owner)
        {
            return owner;
        }
        if (root.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is Control focused)
        {
            foreach (var (key, action) in taskActions)
            {
                if (ReferenceEquals(action, focused))
                {
                    return new CardFocus(key.TaskId, key.Action, focused.FocusState);
                }
            }
        }

        return null;
    }

    internal void RefreshAfterCapture(CardFocus? previousFocus)
    {
        focusGeneration++;
        RequestRefresh(previousFocus is { } card ? new ActionOrigin(card, focusGeneration) : null);
        openMenu?.Hide();
    }

    internal void Refresh() => RequestRefresh();

    private void RequestRefresh(ActionOrigin? origin = null)
    {
        refreshPending = true;
        if (origin is { } request && request.Generation == focusGeneration)
        {
            pendingFocus = request;
        }
        RefreshWhenMenuClosed();
    }

    private void RefreshWhenMenuClosed()
    {
        if (!refreshPending || menusAwaitingClose > 0)
        {
            return;
        }

        refreshPending = false;
        var restore = pendingFocus;
        pendingFocus = null;
        try
        {
            var tasks = store.List(archived);
            RenderTasks(tasks);
            retryChange = null;
            error.IsOpen = false;
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            taskActions.Clear();
            foreach (var lane in lanes)
            {
                lane.Child = null;
            }
            columns.Visibility = Visibility.Collapsed;
            ShowError("Couldn’t load your tasks. Try again when the database is available.", Refresh);
        }

        if (restore is { } origin && origin.Generation == focusGeneration)
        {
            var key = (origin.Card.TaskId, origin.Card.Action);
            var focus = taskActions.GetValueOrDefault(key, archiveButton);
            var state = origin.Card.State == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
            FocusWhenReady(focus, state, () => origin.Generation == focusGeneration && !HasModalEditor &&
                CanRestoreFocus?.Invoke() == true && ReferenceEquals(taskActions.GetValueOrDefault(key, archiveButton), focus));
        }
    }

    private void LayoutLanes(bool stack)
    {
        if (stack == lanesStacked)
        {
            return;
        }

        lanesStacked = stack;
        columns.ColumnSpacing = stack ? 0 : SurfaceStyle.SectionGap;
        columns.RowSpacing = stack ? SurfaceStyle.SectionGap : 0;
        for (var index = 0; index < lanes.Length; index++)
        {
            Grid.SetColumn(lanes[index], stack ? 0 : index);
            Grid.SetColumnSpan(lanes[index], stack ? lanes.Length : 1);
            Grid.SetRow(lanes[index], stack ? index : 0);
        }
    }

    private void RenderTasks(IReadOnlyList<BoardTask> tasks)
    {
        taskActions.Clear();
        columns.Visibility = Visibility.Visible;
        for (var column = 0; column < Columns.Length; column++)
        {
            var (status, label) = Columns[column];
            var columnTasks = tasks.Where(task => task.Status == status).ToArray();
            var title = ManagementStyle.SectionHeading(label);
            var count = ManagementStyle.Label(columnTasks.Length.ToString(), SurfaceStyle.Caption, supporting: true);
            count.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(count, columnTasks.Length == 1 ? "1 task" : $"{columnTasks.Length} tasks");
            var laneHeader = new Grid { Padding = new Thickness(SurfaceStyle.ControlGap), ColumnSpacing = SurfaceStyle.ControlGap };
            laneHeader.ColumnDefinitions.Add(new ColumnDefinition());
            laneHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            laneHeader.Children.Add(title);
            Grid.SetColumn(count, 1);
            laneHeader.Children.Add(count);
            var lane = new StackPanel
            {
                Spacing = SurfaceStyle.ControlGap,
                MinHeight = 96,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            lane.Children.Add(laneHeader);
            var cards = new List<(long Id, Border Element)>();
            for (var index = 0; index < columnTasks.Length; index++)
            {
                var task = columnTasks[index];
                var card = CreateCard(task, index > 0, index < columnTasks.Length - 1);
                cards.Add((task.Id, card));
                lane.Children.Add(card);
            }

            if (columnTasks.Length == 0)
            {
                var empty = (archived, status) switch
                {
                    (true, _) => "No archived tasks",
                    (false, TaskStatus.ToDo) => "Add a task",
                    (false, TaskStatus.InProgress) => "Start a task",
                    _ => "Complete a task",
                };
                var hint = ManagementStyle.BodyText(empty);
                hint.Margin = new Thickness(SurfaceStyle.ControlGap, 0, SurfaceStyle.ControlGap, SurfaceStyle.ControlGap);
                lane.Children.Add(hint);
            }

            lanes[column].Child = lane;
            if (!archived)
            {
                EnableDrop(lane, status, cards);
            }
        }
        if (appearance is { } state)
        {
            ManagementStyle.ApplyContrast(columns, state);
        }
    }

    private Border CreateCard(BoardTask task, bool canMoveUp, bool canMoveDown)
    {
        var title = ManagementStyle.Label(task.Title, SurfaceStyle.StrongBody);
        var body = new StackPanel { Spacing = SurfaceStyle.ControlGap };
        if (archived)
        {
            body.Children.Add(title);
        }
        else
        {
            var actions = new Button
            {
                Content = new SymbolIcon(Symbol.More),
                Style = SurfaceStyle.GetStyle("OpenBreakManagementButtonStyle"),
                MinWidth = 36,
                Padding = new Thickness(6),
                VerticalAlignment = VerticalAlignment.Top,
            };
            actions.Flyout = CreateActionsMenu(task, actions, canMoveUp, canMoveDown);
            AutomationProperties.SetName(actions, $"Actions for {task.Title}");
            AutomationProperties.SetAutomationId(actions, $"TasksActions{task.Id}");
            ToolTipService.SetToolTip(actions, "Task actions");
            taskActions.Add((task.Id, CardAction.Actions), actions);

            var cardHeader = new Grid { ColumnSpacing = SurfaceStyle.ControlGap };
            cardHeader.ColumnDefinitions.Add(new ColumnDefinition());
            cardHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardHeader.Children.Add(title);
            Grid.SetColumn(actions, 1);
            cardHeader.Children.Add(actions);
            body.Children.Add(cardHeader);
        }
        if (task.NextStep is { } nextStep)
        {
            var note = ManagementStyle.BodyText(nextStep);
            body.Children.Add(note);
        }
        if (archived)
        {
            var restore = ManagementStyle.Button("Restore", $"TasksRestore{task.Id}");
            AutomationProperties.SetName(restore, $"Restore {task.Title}");
            restore.Click += (_, _) => ApplyChange(() => store.SetArchived(task.Id, archived: false),
                new ActionOrigin(new CardFocus(task.Id, CardAction.Restore, restore.FocusState), ++focusGeneration));
            taskActions.Add((task.Id, CardAction.Restore), restore);

            var delete = new Button
            {
                Content = new SymbolIcon(Symbol.Delete),
                Style = SurfaceStyle.GetStyle("OpenBreakManagementButtonStyle"),
                MinWidth = 36,
                MinHeight = 36,
                Padding = new Thickness(6),
            };
            AutomationProperties.SetName(delete, $"Delete task: {task.Title}");
            AutomationProperties.SetAutomationId(delete, $"TasksDelete{task.Id}");
            ToolTipService.SetToolTip(delete, "Delete task");
            delete.Click += (_, _) => ApplyChange(() => store.DeleteArchived(task.Id),
                new ActionOrigin(new CardFocus(task.Id, CardAction.Delete, delete.FocusState), ++focusGeneration));
            taskActions.Add((task.Id, CardAction.Delete), delete);

            var actions = new ActionRow();
            actions.Children.Add(restore);
            actions.Children.Add(delete);
            body.Children.Add(actions);
        }

        var card = ManagementStyle.Card(body);
        if (!archived)
        {
            card.CanDrag = true;
            card.DragStarting += (_, args) =>
            {
                draggedTask = task.Id;
                args.Data.SetData(DragFormat, "Task");
                args.Data.RequestedOperation = DataPackageOperation.Move;
            };
            card.DropCompleted += (_, _) => draggedTask = null;
        }
        return card;
    }

    private MenuFlyout CreateActionsMenu(BoardTask task, Button actions, bool canMoveUp, bool canMoveDown)
    {
        var menu = new MenuFlyout();
        Func<ActionOrigin, Task>? selected = null;
        var origin = new ActionOrigin(new CardFocus(task.Id, CardAction.Actions, FocusState.Programmatic), focusGeneration);
        bool awaitingClose = false;
        menu.Opening += (_, _) =>
        {
            origin = new ActionOrigin(origin.Card with { State = actions.FocusState }, ++focusGeneration);
            awaitingClose = true;
            menusAwaitingClose++;
            openMenu = menu;
            menuOwner = origin.Card;
        };
        menu.Closed += (_, _) =>
        {
            awaitingClose = false;
            menusAwaitingClose--;
            if (ReferenceEquals(openMenu, menu))
            {
                openMenu = null;
                menuOwner = null;
            }
            var run = selected;
            selected = null;
            AfterClose(run);
        };

        AddMenuItem("Edit task", focus => EditTask(task, focus));
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var (status, label) in Columns)
        {
            if (status != task.Status)
            {
                AddChange($"Move to {label}", () => store.Move(task.Id, status));
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AddChange("Move up", () => store.MoveWithin(task.Id, -1), canMoveUp);
        AddChange("Move down", () => store.MoveWithin(task.Id, 1), canMoveDown);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddChange("Archive task", () => store.SetArchived(task.Id, archived: true));
        return menu;

        void AddChange(string label, Action change, bool enabled = true) => AddMenuItem(label, focus =>
        {
            ApplyChange(change, focus);
            return Task.CompletedTask;
        }, enabled);

        void AddMenuItem(string label, Func<ActionOrigin, Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                if (awaitingClose)
                {
                    selected = action;
                }
                else
                {
                    AfterClose(action);
                }
            };
            menu.Items.Add(item);
        }

        void AfterClose(Func<ActionOrigin, Task>? action)
        {
            // Let the native flyout return focus before replacing its anchor or opening a dialog.
            var saved = origin;
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (action is not null)
                {
                    await action(saved);
                }
                RefreshWhenMenuClosed();
            });
        }
    }

    private void EnableDrop(StackPanel lane, TaskStatus status, List<(long Id, Border Element)> cards)
    {
        lane.AllowDrop = true;
        lane.DragOver += (_, args) =>
        {
            if (draggedTask.HasValue && args.DataView.Contains(DragFormat))
            {
                args.AcceptedOperation = DataPackageOperation.Move;
                args.Handled = true;
            }
        };
        lane.Drop += (_, args) =>
        {
            if (draggedTask is not long id || !args.DataView.Contains(DragFormat))
            {
                return;
            }

            args.Handled = true;
            draggedTask = null;
            var y = args.GetPosition(lane).Y;
            long? beforeId = null;
            foreach (var (cardId, element) in cards)
            {
                var top = element.TransformToVisual(lane).TransformPoint(new Point()).Y;
                if (cardId != id && y < top + element.ActualHeight / 2)
                {
                    beforeId = cardId;
                    break;
                }
            }

            ApplyChange(() => store.Move(id, status, beforeId),
                new ActionOrigin(new CardFocus(id, CardAction.Actions, FocusState.Programmatic), ++focusGeneration));
        };
    }

    private async Task EditTask(BoardTask task, ActionOrigin origin)
    {
        if (origin.Generation != focusGeneration || HasModalEditor || archived || CanRestoreFocus?.Invoke() != true)
        {
            return;
        }

        var title = new TextBox { Header = "Title", Text = task.Title, MaxLength = TaskStore.TitleLimit };
        var nextStep = new TextBox
        {
            Header = "Next step (optional)",
            Text = task.NextStep ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100,
            MaxHeight = 220,
            MaxLength = TaskStore.NextStepLimit,
        };
        var feedback = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var form = new StackPanel { Spacing = SurfaceStyle.ContentGap };
        form.Children.Add(title);
        form.Children.Add(nextStep);
        form.Children.Add(feedback);
        var dialog = new ContentDialog
        {
            Style = SurfaceStyle.GetStyle("OpenBreakTaskDialogStyle"),
            XamlRoot = root.XamlRoot,
            RequestedTheme = dialogTheme,
            Title = "Edit task",
            Content = form,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            PrimaryButtonStyle = SurfaceStyle.GetStyle("OpenBreakManagementPrimaryButtonStyle"),
            CloseButtonStyle = SurfaceStyle.GetStyle("OpenBreakManagementButtonStyle"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogTopOverlay"] = SurfaceStyle.Brush(0, 0);
        AutomationProperties.SetAutomationId(dialog, "TasksEditDialog");
        AutomationProperties.SetAutomationId(title, "TasksEditTitle");
        AutomationProperties.SetAutomationId(nextStep, "TasksEditNextStep");
        dialog.Opened += (_, _) =>
        {
            if (CanRestoreFocus?.Invoke() == true)
            {
                title.Focus(FocusState.Programmatic);
            }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text))
            {
                feedback.Message = "Enter a task title.";
                feedback.IsOpen = true;
                title.Focus(FocusState.Programmatic);
                args.Cancel = true;
                return;
            }

            try
            {
                store.Edit(task.Id, title.Text, nextStep.Text);
            }
            catch (Exception exception) when (TaskStore.IsStorageError(exception))
            {
                feedback.Message = "Couldn’t save this task. Try again.";
                feedback.IsOpen = true;
                args.Cancel = true;
            }
        };

        activeDialog = dialog;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            activeDialog = null;
        }
        if (result == ContentDialogResult.Primary)
        {
            RequestRefresh(origin);
        }
    }

    private void ApplyChange(Action action, ActionOrigin origin)
    {
        try
        {
            action();
            RequestRefresh(origin);
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            ShowError("Couldn’t save the change. Try again.",
                () => ApplyChange(action, origin with { Generation = ++focusGeneration }));
        }
    }

    private static void FocusWhenReady(Control focus, FocusState state, Func<bool>? canFocus = null)
    {
        if (focus.IsLoaded)
        {
            TryFocus();
            return;
        }

        focus.Loaded += FocusWhenLoaded;
        void FocusWhenLoaded(object sender, RoutedEventArgs args)
        {
            focus.Loaded -= FocusWhenLoaded;
            TryFocus();
        }

        void TryFocus()
        {
            if (canFocus?.Invoke() != false)
            {
                focus.Focus(state);
            }
        }
    }

    private void ShowError(string message, Action action)
    {
        error.Message = message;
        retryChange = action;
        error.ActionButton = retry;
        error.IsOpen = true;
    }

}
