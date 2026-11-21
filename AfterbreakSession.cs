using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Afterbreak;

internal sealed class AfterbreakSession
{
    private static readonly TimeSpan PreviewLength = TimeSpan.FromSeconds(10);
    private readonly Desktop desktop;
    private readonly CaptureWindow capture;
    private readonly WarningWindow warning;
    private readonly BreakWindow preview;
    private readonly DispatcherQueueTimer timer;
    private readonly Action exit;
    private readonly TaskStore tasks;
    private BoardWindow? board;
    private Surface visible;
    private BreakLayout previewLayout;
    private long startedAt;
    private nint returnWindow;
    private BoardWindow.CardFocus? captureCardFocus;

    internal AfterbreakSession(DispatcherQueue dispatcher, Action exit, TaskStore? tasks = null)
    {
        this.exit = exit;
        this.tasks = tasks ?? TaskStore.ForCurrentUser();
        capture = new CaptureWindow(SaveTask, Dismiss);
        warning = new WarningWindow();
        preview = new BreakWindow(Dismiss);
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += Tick;

        desktop = new Desktop();
        desktop.Command += Execute;
    }

    private enum Surface
    {
        None,
        Capture,
        Warning,
        Preview,
    }

    private void Execute(TrayCommand command, nint source)
    {
        switch (command)
        {
            case TrayCommand.Tasks:
                if (visible is Surface.Warning or Surface.Preview)
                {
                    Dismiss();
                }

                board ??= new BoardWindow(tasks, () => Execute(TrayCommand.Capture, desktop.BoardHandle));
                desktop.BoardHandle = board.Handle;
                board.Show(source);
                break;
            case TrayCommand.Capture:
                if (visible == Surface.Capture)
                {
                    if (source != 0 && source != returnWindow)
                    {
                        SetCaptureSource(source);
                    }
                    capture.BringBack(returnWindow);
                    return;
                }

                Dismiss();
                SetCaptureSource(source);
                visible = Surface.Capture;
                desktop.PreviewAvailable = false;
                capture.Show(source);
                break;
            case TrayCommand.Preview:
            case TrayCommand.PreviewSampleBoard:
                if (visible != Surface.None)
                {
                    return;
                }

                Dismiss();
                previewLayout = command == TrayCommand.Preview ? BreakLayout.Compact : BreakLayout.SampleBoard;
                returnWindow = source;
                visible = Surface.Warning;
                desktop.PreviewAvailable = false;
                desktop.DismissLabel = "Cancel preview";
                warning.Show(returnWindow);
                StartClock();
                break;
            case TrayCommand.EndBreak:
                if (visible is Surface.Warning or Surface.Preview)
                {
                    Dismiss();
                }
                break;
            case TrayCommand.Quit:
                Dismiss();
                desktop.Dispose();
                capture.Close();
                warning.Close();
                preview.Close();
                board?.CloseForQuit();
                exit();
                break;
        }
    }

    private void StartClock()
    {
        startedAt = Stopwatch.GetTimestamp();
        timer.Start();
    }

    private void SetCaptureSource(nint source)
    {
        returnWindow = source;
        captureCardFocus = board is not null && source == board.Handle ? board.GetFocusedCard() : null;
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        var remaining = PreviewLength - Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero)
        {
            var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            if (visible == Surface.Warning)
            {
                warning.Update(seconds);
            }
            else if (visible == Surface.Preview)
            {
                preview.Update(seconds);
            }
            return;
        }

        timer.Stop();
        if (visible == Surface.Warning)
        {
            warning.AppWindow.Hide();
            var task = previewLayout == BreakLayout.Compact ? GetReturnTask() : null;
            returnWindow = desktop.WorkWindow();
            visible = Surface.Preview;
            desktop.DismissLabel = "End break";
            preview.Show(returnWindow, previewLayout, task?.Title, task?.NextStep);
            StartClock();
        }
        else if (visible == Surface.Preview)
        {
            Dismiss();
        }
    }

    private BoardTask? GetReturnTask()
    {
        try
        {
            return tasks.ReturnTask();
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            return null;
        }
    }

    private bool SaveTask(string text)
    {
        try
        {
            tasks.Add(text);
        }
        catch (Exception exception) when (TaskStore.IsStorageError(exception))
        {
            return false;
        }

        var cardFocus = captureCardFocus;
        Dismiss();
        if (board?.AppWindow.IsVisible == true)
        {
            board.RefreshAfterCapture(cardFocus);
        }
        return true;
    }

    private void Dismiss()
    {
        var closing = visible;
        visible = Surface.None;
        timer.Stop();
        desktop.DismissLabel = null;
        desktop.PreviewAvailable = true;
        switch (closing)
        {
            case Surface.Capture:
                SurfaceWindow.HideAndReturn(capture, returnWindow);
                break;
            case Surface.Warning:
                warning.AppWindow.Hide();
                break;
            case Surface.Preview:
                SurfaceWindow.HideAndReturn(preview, returnWindow);
                break;
        }
        returnWindow = 0;
        captureCardFocus = null;
    }
}
