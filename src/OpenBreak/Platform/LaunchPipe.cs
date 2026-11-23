using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBreak;

internal static class LaunchArguments
{
    internal const string StartupFlag = "--startup";
    // Share the single-instance guard with installed builds under the previous name.
    internal const string InstanceMutexName = @"Local\Afterbreak.NativeStudy";

    internal static bool RevealMain(string[] args) => !args.Contains(StartupFlag, StringComparer.Ordinal);

    internal static Task<bool> RelayDuplicateAsync(string[] args, Func<Task<bool>> relay) =>
        RevealMain(args) ? relay() : Task.FromResult(false);
}

internal sealed class LaunchPipe(string name, uint sessionId)
{
    // The v1 reveal endpoint must still reach an instance under the previous name.
    private const string RevealPipePrefix = "Afterbreak.Reveal.v1.Session";
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private const byte RevealByte = 1;
    private const byte AcceptedByte = 2;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);

    internal static LaunchPipe ForCurrentSession()
    {
        using var process = Process.GetCurrentProcess();
        var session = (uint)process.SessionId;
        return new LaunchPipe(NameForSession(session), session);
    }

    internal static string NameForSession(uint session) =>
        RevealPipePrefix + session.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal async Task<bool> RequestRevealAsync(TimeSpan connectTimeout, Action<uint>? grantForeground = null,
        CancellationToken cancellation = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, Options);
            using (var connectionBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                connectionBudget.CancelAfter(connectTimeout);
                await client.ConnectAsync(connectionBudget.Token).ConfigureAwait(false);
            }
            if (!GetNamedPipeServerSessionId(client.SafePipeHandle, out var peerSession) || peerSession != sessionId)
            {
                return false;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(ReplyTimeout);
            if (grantForeground is not null &&
                GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId) && processId != 0)
            {
                grantForeground(processId);
            }

            byte[] message = [RevealByte];
            await client.WriteAsync(message, deadline.Token).ConfigureAwait(false);
            return await client.ReadAsync(message, deadline.Token).ConfigureAwait(false) == 1 &&
                message[0] == AcceptedByte;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    internal async Task ListenAsync(Func<CancellationToken, Task<bool>> queueReveal, CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream listener;
            try
            {
                listener = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    Options | PipeOptions.FirstPipeInstance);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Initial reveal and the tray still work if the endpoint cannot be reserved.
                return;
            }
            using (listener)
            {
                try
                {
                    await listener.WaitForConnectionAsync(shutdown).ConfigureAwait(false);
                    if (!GetNamedPipeClientSessionId(listener.SafePipeHandle, out var peerSession) ||
                        peerSession != sessionId)
                    {
                        continue;
                    }

                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                    deadline.CancelAfter(IdleTimeout);
                    var message = new byte[1];
                    if (await listener.ReadAsync(message, deadline.Token).ConfigureAwait(false) == 1 &&
                        message[0] == RevealByte && await queueReveal(deadline.Token).ConfigureAwait(false))
                    {
                        message[0] = AcceptedByte;
                        await listener.WriteAsync(message, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                {
                    // An idle or disconnected client gets no more than this connection.
                }
            }
        }
    }

    internal static void AllowForeground(uint processId) => _ = AllowSetForegroundWindow(processId);

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint session);

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint process);
}

internal sealed class LaunchRevealQueue(Func<Action, bool> enqueue, Action reveal, CancellationToken shutdown)
{
    private readonly object gate = new();
    private PendingReveal? pending;
    private bool posted;

    private sealed record PendingReveal(TaskCompletionSource<bool> Result, CancellationToken Deadline);

    // The listener handles one client at a time. Replace an expired request without adding
    // another callback if the UI thread is still busy.
    internal Task<bool> RequestAsync(CancellationToken deadline)
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested || deadline.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = new PendingReveal(result, deadline);
            if (!posted)
            {
                posted = true;
                if (!enqueue(Drain))
                {
                    posted = false;
                    pending = null;
                    return Task.FromResult(false);
                }
            }
            return result.Task.WaitAsync(deadline);
        }
    }

    private void Drain()
    {
        PendingReveal? request;
        lock (gate)
        {
            posted = false;
            request = pending;
            pending = null;
        }
        if (request is null)
        {
            return;
        }
        var accepted = !shutdown.IsCancellationRequested && !request.Deadline.IsCancellationRequested;
        request.Result.TrySetResult(accepted);
        if (accepted)
        {
            reveal();
        }
    }
}
