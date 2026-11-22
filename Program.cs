using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Afterbreak;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = new Mutex(true, @"Local\Afterbreak.NativeStudy", out var firstInstance);
        if (!firstInstance)
        {
            return;
        }

        var captureMotionStudy = args.Contains("--capture-motion-study", StringComparer.Ordinal);
        using var diagnostics = BreakDiagnostics.Open(args);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            new AfterbreakApp(captureMotionStudy, diagnostics);
        });
    }
}

public sealed partial class AfterbreakApp : Application
{
    private readonly bool captureMotionStudy;
    private readonly BreakDiagnostics? diagnostics;
    private AfterbreakSession? session;

    internal AfterbreakApp(bool captureMotionStudy = false, BreakDiagnostics? diagnostics = null)
    {
        this.captureMotionStudy = captureMotionStudy;
        this.diagnostics = diagnostics;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        session = new AfterbreakSession(DispatcherQueue.GetForCurrentThread(), Exit, captureMotionStudy: captureMotionStudy,
            diagnostics: diagnostics);
    }
}
