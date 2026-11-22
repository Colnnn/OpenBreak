using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Afterbreak;

internal static class Program
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(6);
    private static readonly uint SessionId = CurrentSessionId();

    private static async Task Main(string[] args)
    {
        if (args is ["--worker", var pipeName] && pipeName.StartsWith("Afterbreak.Check.", StringComparison.Ordinal))
        {
            await RunServerProcess(pipeName);
            return;
        }
        if (args is ["--duplicate", var endpoint, .. var options] &&
            endpoint.StartsWith("Afterbreak.Check.", StringComparison.Ordinal))
        {
            await LaunchArguments.RelayDuplicateAsync(options,
                () => new LaunchPipe(endpoint, SessionId).RequestRevealAsync(TestBudget));
            return;
        }
        Check(LaunchArguments.RevealMain([]) && LaunchArguments.RevealMain(["--capture-motion-study"]) &&
            LaunchArguments.RevealMain(["--diagnostics", "trace.log"]) && LaunchArguments.RevealMain(["--Startup"]),
            "normal, diagnostic and motion launches reveal unless the flag matches exactly");
        Check(!LaunchArguments.RevealMain(["--startup"]) &&
            !LaunchArguments.RevealMain(["--startup", "--capture-motion-study"]), "the quiet startup flag suppresses any reveal");
        await CheckQueue();
        await CheckProcessRelay();
        await CheckQueuedRequestExpiry();
        await CheckIdleAndInvalidClients();
        await CheckSessionValidation();
        await CheckAbsentListener();
    }

    private static async Task CheckQueue()
    {
        var actions = new Queue<Action>();
        var reveals = 0;
        using var shutdown = new CancellationTokenSource();
        var queue = new LaunchRevealQueue(action => { actions.Enqueue(action); return true; },
            () => reveals++, shutdown.Token);
        using var expired = new CancellationTokenSource();
        var first = queue.RequestAsync(expired.Token);
        expired.Cancel();
        try { await first; }
        catch (OperationCanceledException) { }
        actions.Dequeue()();
        Check(reveals == 0, "an expired reveal never activates later when the dispatcher resumes");

        using var abandoned = new CancellationTokenSource();
        first = queue.RequestAsync(abandoned.Token);
        abandoned.Cancel();
        try { await first; }
        catch (OperationCanceledException) { }
        var second = queue.RequestAsync(CancellationToken.None);
        Check(actions.Count == 1 && !second.IsCompleted, "a newer request reuses the queued callback without premature acknowledgement");
        actions.Dequeue()();
        Check(await second && reveals == 1, "a live dispatcher accepts and reveals once");

        var stopping = queue.RequestAsync(CancellationToken.None);
        shutdown.Cancel();
        actions.Dequeue()();
        Check(!await stopping && reveals == 1 && !await queue.RequestAsync(CancellationToken.None),
            "shutdown rejects queued and subsequent reveals");
        var rejected = new LaunchRevealQueue(_ => false, () => reveals++, CancellationToken.None);
        Check(!await rejected.RequestAsync(CancellationToken.None) && reveals == 1,
            "a closed dispatcher rejects a launch request");
    }

    private static async Task CheckProcessRelay()
    {
        var pipeName = TestPipeName();
        uint foregroundRecipient = 0;
        // Start the request before creating the listener, just as with a second launch during startup.
        var connect = new LaunchPipe(pipeName, SessionId).RequestRevealAsync(TestBudget, pid => foregroundRecipient = pid);
        using var child = StartChild("--worker", pipeName);
        try
        {
            Check(await connect && foregroundRecipient == (uint)child.Id, "a delayed synthetic process accepts a launch and supplies its actual PID");
            Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TestBudget) == "revealed",
                "the accepted pipe command reaches the existing process");
            using var quiet = StartChild("--duplicate", pipeName, "--startup");
            await quiet.WaitForExitAsync().WaitAsync(TestBudget);
            var additional = child.StandardOutput.ReadLineAsync();
            var responded = await Task.WhenAny(additional, Task.Delay(200)) == additional;
            Check(quiet.ExitCode == 0 && !responded, "a quiet duplicate process exits without sending a reveal");
        }
        finally
        {
            child.StandardInput.Close();
            using var wait = new CancellationTokenSource(TestBudget);
            await child.WaitForExitAsync(wait.Token);
        }
        Check(child.ExitCode == 0, "the isolated listener exits cleanly before process shutdown");
    }

    private static Process StartChild(params string[] arguments)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated worker.");
    }

    private static async Task CheckQueuedRequestExpiry()
    {
        var channel = new LaunchPipe(TestPipeName(), SessionId);
        var callbacks = new ConcurrentQueue<Action>();
        var reveals = 0;
        using var stopping = new CancellationTokenSource(TestBudget);
        var activations = new LaunchRevealQueue(action =>
        {
            callbacks.Enqueue(action);
            return true;
        }, () => reveals++, stopping.Token);
        var listener = channel.ListenAsync(activations.RequestAsync, stopping.Token);
        Check(!await channel.RequestRevealAsync(TestBudget) && callbacks.Count == 1,
            "a valid pipe command times out without acknowledgement from a stalled dispatcher");
        if (!callbacks.TryDequeue(out var expired))
        {
            throw new InvalidOperationException("The expired request was not queued.");
        }
        expired();
        Check(reveals == 0, "draining the expired pipe callback cannot reveal the main window");
        stopping.Cancel();
        await listener.WaitAsync(TestBudget);

        using var next = new CancellationTokenSource(TestBudget);
        listener = channel.ListenAsync(_ => Task.FromResult(true), next.Token);
        Check(await channel.RequestRevealAsync(TestBudget), "a new listener can reserve the endpoint after shutdown");
        next.Cancel();
        await listener.WaitAsync(TestBudget);
    }

    private static async Task RunServerProcess(string pipeName)
    {
        using var stopping = new CancellationTokenSource(TestBudget + TestBudget);
        var server = new LaunchPipe(pipeName, SessionId).ListenAsync(_ =>
        {
            Console.WriteLine("revealed");
            return Task.FromResult(true);
        }, stopping.Token);
        try
        {
            await Console.In.ReadLineAsync().WaitAsync(stopping.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            stopping.Cancel();
            await server;
        }
    }

    private static async Task CheckIdleAndInvalidClients()
    {
        var name = TestPipeName();
        var channel = new LaunchPipe(name, SessionId);
        using var stopping = new CancellationTokenSource(TestBudget);
        var calls = 0;
        var server = channel.ListenAsync(_ => Task.FromResult(++calls > 0), stopping.Token);
        using var idle = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await idle.ConnectAsync(stopping.Token);
        Check(await channel.RequestRevealAsync(TestBudget, cancellation: stopping.Token) && calls == 1,
            "an idle connection expires and the next legitimate launch succeeds");

        using var invalid = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await invalid.ConnectAsync(stopping.Token);
        var message = new byte[] { 42 };
        await invalid.WriteAsync(message, stopping.Token);
        Check(await invalid.ReadAsync(message, stopping.Token) == 0 && calls == 1,
            "an unsupported command disconnects without revealing or acknowledging");

        stopping.Cancel();
        await server.WaitAsync(TestBudget);
        Check(server.IsCompletedSuccessfully, "listener cancellation finishes while waiting for the next client");
    }

    private static async Task CheckSessionValidation()
    {
        var name = TestPipeName();
        var grants = 0;
        var calls = 0;
        using var stopping = new CancellationTokenSource(TestBudget);
        var server = new LaunchPipe(name, SessionId).ListenAsync(_ => Task.FromResult(++calls > 0), stopping.Token);
        Check(!await new LaunchPipe(name, SessionId + 1).RequestRevealAsync(TestBudget, _ => grants++) &&
            grants == 0 && calls == 0, "a client rejects a mismatched server session before granting foreground or sending");
        stopping.Cancel();
        await server.WaitAsync(TestBudget);

        using var other = new CancellationTokenSource(TestBudget);
        server = new LaunchPipe(name, SessionId + 1).ListenAsync(_ => Task.FromResult(++calls > 0), other.Token);
        try
        {
            Check(!await new LaunchPipe(name, SessionId).RequestRevealAsync(TestBudget) && calls == 0,
                "a listener rejects a client whose actual session differs from its configured session");
        }
        finally
        {
            other.Cancel();
            await server.WaitAsync(TestBudget);
        }
    }

    private static async Task CheckAbsentListener()
    {
        var watch = Stopwatch.StartNew();
        Check(!await new LaunchPipe(TestPipeName(), SessionId).RequestRevealAsync(TimeSpan.FromMilliseconds(250)) &&
            watch.Elapsed < TestBudget, "an older instance without a pipe does not hang the duplicate launch");
    }

    private static uint CurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return (uint)process.SessionId;
    }

    private static string TestPipeName() => $"Afterbreak.Check.{Guid.NewGuid():N}";

    private static void Check(bool passed, string message)
    {
        if (!passed)
        {
            throw new InvalidOperationException(message);
        }
        Console.WriteLine($"PASS {message}");
    }
}
