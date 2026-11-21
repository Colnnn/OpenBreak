using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Afterbreak;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Local\Afterbreak.NativeStudy", out var firstInstance);
        if (!firstInstance)
        {
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            new AfterbreakApp();
        });
    }
}

internal sealed class AfterbreakApp : Application
{
    private AfterbreakSession? session;

    internal AfterbreakApp()
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        session = new AfterbreakSession(DispatcherQueue.GetForCurrentThread(), Exit);
    }
}
