using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class BoardWindow : Window
{
    internal enum CardAction { Edit, Move, Restore }

    internal readonly record struct CardFocus(long TaskId, CardAction Action, FocusState State);

    private const string DragFormat = "Afterbreak.BoardTask";
    private static readonly (TaskStatus Status, string Label)[] Columns =
    [
        (TaskStatus.ToDo, "To do"),
        (TaskStatus.InProgress, "In progress"),
        (TaskStatus.Done, "Done"),
    ];

    private readonly TaskStore store;
    private readonly Grid root;
    private readonly Grid columns;
    private readonly TextBlock heading;
    private readonly Button archiveButton;
    private readonly ScrollViewer scroll;
    private readonly InfoBar error;
    private readonly Button retry;
    private readonly Dictionary<(long TaskId, CardAction Action), Control> taskActions = [];
    private readonly Border[] lanes = new Border[Columns.Length];
    private bool archived;
    private bool placed;
    private bool quitting;
    private bool editing;
    private bool lanesStacked;
    private long? draggedTask;

    internal BoardWindow(TaskStore store, Action? addTask = null)
    {
        this.store = store;
        Title = "Afterbreak · Tasks";
        SystemBackdrop = new MicaBackdrop();

        heading = SurfaceStyle.Label("Tasks", 30, FontWeights.SemiBold);
        heading.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        var addButton = new Button { Content = "Add task", VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
        {
            addButton.Style = style;
        }
        addButton.Click += (_, _) => addTask?.Invoke();
        archiveButton = new Button { Content = "Archive", VerticalAlignment = VerticalAlignment.Center };

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerActions.Children.Add(archiveButton);
        headerActions.Children.Add(addButton);
        var header = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.Children.Add(heading);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        header.SizeChanged += (_, args) =>
        {
            var stack = args.NewSize.Width < 330;
            Grid.SetColumn(headerActions, stack ? 0 : 1);
            Grid.SetRow(headerActions, stack ? 1 : 0);
        };

        retry = new Button { Content = "Retry" };
        retry.Click += (_, _) => Refresh();
        error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, Title = "Task storage" };

        columns = new Grid { ColumnSpacing = 12 };
        for (var index = 0; index < Columns.Length; index++)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lanes[index] = TaskPanel();
            Grid.SetColumn(lanes[index], index);
            columns.Children.Add(lanes[index]);
        }
        columns.SizeChanged += (_, args) => LayoutLanes(args.NewSize.Width < 690);

        scroll = new ScrollViewer
        {
            Content = columns,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        archiveButton.Click += (_, _) =>
        {
            archived = !archived;
            heading.Text = archived ? "Archive" : "Tasks";
            archiveButton.Content = archived ? "Back to board" : "Archive";
            addButton.Visibility = archived ? Visibility.Collapsed : Visibility.Visible;
            Refresh();
            scroll.ChangeView(null, 0, null, disableAnimation: true);
        };
        root = new Grid { Padding = new Thickness(20), RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        Grid.SetRow(error, 1);
        root.Children.Add(error);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Content = root;

        AppWindow.Closing += (_, args) =>
        {
            if (!quitting)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    internal void Show(nint source)
    {
        if (!placed)
        {
            SurfaceWindow.Place(this, source, 1020, 660);
            placed = true;
        }

        Refresh();
        Activate();
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show();
        }
    }

    internal void CloseForQuit()
    {
        quitting = true;
        Close();
    }

    internal CardFocus? GetFocusedCard()
    {
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
        Refresh();
        if (previousFocus is { } card)
        {
            var key = (card.TaskId, card.Action);
            var focus = taskActions.GetValueOrDefault(key, archiveButton);
            var state = card.State == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
            FocusWhenReady(focus, state, () => !editing && AppWindow.IsVisible && Desktop.IsForeground(Handle) &&
                ReferenceEquals(taskActions.GetValueOrDefault(key, archiveButton), focus));
        }
    }

    internal void Refresh()
    {
        try
        {
            var tasks = store.List(archived);
            RenderTasks(tasks);
            error.IsOpen = false;
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            taskActions.Clear();
            foreach (var lane in lanes)
            {
                lane.Child = null;
            }
            scroll.Visibility = Visibility.Collapsed;
            ShowError("Couldn’t load your tasks. Try again when the database is available.", canRetry: true);
        }
    }

    private void LayoutLanes(bool stack)
    {
        if (stack == lanesStacked)
        {
            return;
        }

        lanesStacked = stack;
        columns.ColumnSpacing = stack ? 0 : 12;
        columns.RowSpacing = stack ? 12 : 0;
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
        scroll.Visibility = Visibility.Visible;
        for (var column = 0; column < Columns.Length; column++)
        {
            var (status, label) = Columns[column];
            var title = SurfaceStyle.Label(label, 18, FontWeights.SemiBold);
            title.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
            var lane = new StackPanel { Spacing = 10, MinHeight = 96, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            lane.Children.Add(title);
            var columnTasks = tasks.Where(task => task.Status == status).ToArray();
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
                lane.Children.Add(SurfaceStyle.SecondaryLabel(archived ? "No archived tasks" : "No tasks", 14));
            }

            lanes[column].Child = lane;
            if (!archived)
            {
                EnableDrop(lane, status, cards);
            }
        }
    }

    private Border CreateCard(BoardTask task, bool canMoveUp, bool canMoveDown)
    {
        var title = SurfaceStyle.Label(task.Title, 16, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(title);
        if (task.NextStep is { } nextStep)
        {
            var note = SurfaceStyle.SecondaryLabel(nextStep, 14);
            note.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(note);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (archived)
        {
            var restore = new Button { Content = "Restore" };
            AutomationProperties.SetName(restore, $"Restore {task.Title}");
            restore.Click += (_, _) => ApplyChange(() => store.SetArchived(task.Id, archived: false));
            actions.Children.Add(restore);
            taskActions.Add((task.Id, CardAction.Restore), restore);
        }
        else
        {
            var edit = new Button { Content = "Edit" };
            AutomationProperties.SetName(edit, $"Edit {task.Title}");
            edit.Click += async (_, _) => await EditTask(task);
            actions.Children.Add(edit);
            taskActions.Add((task.Id, CardAction.Edit), edit);
            var move = new DropDownButton { Content = "Move", Flyout = CreateMoveMenu(task, canMoveUp, canMoveDown) };
            AutomationProperties.SetName(move, $"Move {task.Title}");
            actions.Children.Add(move);
            taskActions.Add((task.Id, CardAction.Move), move);
        }
        body.Children.Add(actions);

        var card = TaskPanel();
        card.Child = body;
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

    private MenuFlyout CreateMoveMenu(BoardTask task, bool canMoveUp, bool canMoveDown)
    {
        var menu = new MenuFlyout();
        foreach (var (status, label) in Columns)
        {
            if (status != task.Status)
            {
                AddMenuItem(menu, $"Move to {label}", () => ApplyChange(() => store.Move(task.Id, status), task.Id));
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(menu, "Move up", () => ApplyChange(() => store.MoveWithin(task.Id, -1), task.Id), canMoveUp);
        AddMenuItem(menu, "Move down", () => ApplyChange(() => store.MoveWithin(task.Id, 1), task.Id), canMoveDown);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(menu, "Archive task", () => ApplyChange(() => store.SetArchived(task.Id, archived: true)));
        return menu;
    }

    private static void AddMenuItem(MenuFlyout menu, string text, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
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

            ApplyChange(() => store.Move(id, status, beforeId), id);
        };
    }

    private async Task EditTask(BoardTask task)
    {
        if (editing)
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
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(title);
        form.Children.Add(nextStep);
        form.Children.Add(feedback);
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = "Edit task",
            Content = form,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => title.Focus(FocusState.Programmatic);
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

        editing = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                RefreshAndFocus(task.Id);
            }
        }
        finally
        {
            editing = false;
        }
    }

    private void ApplyChange(Action action, long? id = null)
    {
        try
        {
            action();
            RefreshAndFocus(id);
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            ShowError("Couldn’t save the change. Try again.", canRetry: false);
        }
    }

    private void RefreshAndFocus(long? id)
    {
        Refresh();
        var focus = id is long taskId ? taskActions.GetValueOrDefault((taskId, CardAction.Move), archiveButton) : archiveButton;
        FocusWhenReady(focus, FocusState.Programmatic);
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

    private void ShowError(string message, bool canRetry)
    {
        error.Message = message;
        error.ActionButton = canRetry ? retry : null;
        error.IsOpen = true;
    }

    private static Border TaskPanel() => new() { Style = (Style)Application.Current.Resources["AfterbreakTaskPanelStyle"] };
}
