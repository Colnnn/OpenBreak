using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

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
    private Surface visible;
    private long startedAt;
    private nint returnWindow;
    private string? task;

    internal AfterbreakSession(DispatcherQueue dispatcher, Action exit)
    {
        this.exit = exit;
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
            case TrayCommand.Capture:
                if (visible == Surface.Capture)
                {
                    if (source != 0)
                    {
                        returnWindow = source;
                    }
                    capture.BringBack(returnWindow);
                    return;
                }

                Dismiss();
                returnWindow = source;
                visible = Surface.Capture;
                capture.Show(source);
                break;
            case TrayCommand.Preview:
                if (visible is Surface.Warning or Surface.Preview)
                {
                    return;
                }

                Dismiss();
                returnWindow = source;
                visible = Surface.Warning;
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
                exit();
                break;
        }
    }

    private void StartClock()
    {
        startedAt = Stopwatch.GetTimestamp();
        timer.Start();
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
            returnWindow = desktop.WorkWindow();
            visible = Surface.Preview;
            desktop.DismissLabel = "End break";
            preview.Show(returnWindow, task);
            StartClock();
        }
        else if (visible == Surface.Preview)
        {
            Dismiss();
        }
    }

    private void SaveTask(string text)
    {
        task = text;
        Dismiss();
    }

    private void Dismiss()
    {
        var closing = visible;
        visible = Surface.None;
        timer.Stop();
        desktop.DismissLabel = null;
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
    }
}
