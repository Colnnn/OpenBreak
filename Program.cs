using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Afterbreak;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var revealMain = LaunchArguments.RevealMain(args);
        var instance = new Mutex(true, @"Local\Afterbreak.NativeStudy", out var firstInstance);
        if (!firstInstance)
        {
            instance.Dispose();
            LaunchArguments.RelayDuplicateAsync(args,
                () => LaunchPipe.ForCurrentSession().RequestRevealAsync(TimeSpan.FromSeconds(4), LaunchPipe.AllowForeground))
                .GetAwaiter().GetResult();
            return;
        }

        using var owner = instance;
        using var shutdown = new CancellationTokenSource();
        var captureMotionStudy = args.Contains("--capture-motion-study", StringComparer.Ordinal);
        using var diagnostics = BreakDiagnostics.Open(args);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        AfterbreakApp? app = null;
        try
        {
            Application.Start(initialization =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                app = new AfterbreakApp(revealMain, captureMotionStudy, diagnostics, shutdown);
            });
        }
        finally
        {
            shutdown.Cancel();
            app?.Listener?.GetAwaiter().GetResult();
        }
    }
}

public sealed partial class AfterbreakApp : Application
{
    private readonly bool captureMotionStudy;
    private readonly bool revealMain;
    private readonly BreakDiagnostics? diagnostics;
    private readonly CancellationTokenSource shutdown;
    private AfterbreakSession? session;

    internal Task? Listener { get; private set; }

    internal AfterbreakApp(bool revealMain, bool captureMotionStudy, BreakDiagnostics? diagnostics,
        CancellationTokenSource shutdown)
    {
        this.revealMain = revealMain;
        this.captureMotionStudy = captureMotionStudy;
        this.diagnostics = diagnostics;
        this.shutdown = shutdown;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        session = new AfterbreakSession(dispatcher, Quit, captureMotionStudy: captureMotionStudy,
            diagnostics: diagnostics, playBreakCue: BreakStartSound.Play);
        var queue = new LaunchRevealQueue(action => dispatcher.TryEnqueue(() => action()),
            session.RevealFromLaunch, shutdown.Token);
        Listener = LaunchPipe.ForCurrentSession().ListenAsync(queue.RequestAsync, shutdown.Token);
        if (revealMain)
        {
            session.RevealFromLaunch();
        }
    }

    private void Quit()
    {
        shutdown.Cancel();
        Exit();
    }
}
