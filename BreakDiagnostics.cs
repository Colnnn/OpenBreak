using System.Security;

namespace Afterbreak;

internal sealed class BreakDiagnostics : IDisposable
{
    private readonly StreamWriter output;
    private bool failed;

    private BreakDiagnostics(StreamWriter output) => this.output = output;

    internal static BreakDiagnostics? Open(string[] args)
    {
        var index = Array.IndexOf(args, "--diagnostics");
        if (index < 0 || index == args.Length - 1 ||
            Array.LastIndexOf(args, "--diagnostics") != index)
        {
            return null;
        }

        try
        {
            var path = args[index + 1];
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
                path.IndexOf(':', 2) >= 0 || !Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var fullPath = Path.GetFullPath(path);
            var parent = Directory.GetParent(fullPath);
            for (var directory = parent; directory is not null; directory = directory.Parent)
            {
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return null;
                }
            }
            var diagnostics = new BreakDiagnostics(new StreamWriter(File.Open(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)));
            diagnostics.Write("event=session-start");
            return diagnostics;
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return null;
        }
    }

    internal void Write(string entry)
    {
        if (failed)
        {
            return;
        }
        try
        {
            output.WriteLine($"{DateTimeOffset.UtcNow:O} {entry}");
            output.Flush();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            failed = true;
        }
    }

    public void Dispose()
    {
        try
        {
            output.Dispose();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            // A diagnostic destination can disappear while the app is running.
        }
    }

    private static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or SecurityException;
}

internal readonly record struct BreakEligibilitySample(ReminderPause Pause, bool? InputDesktop, int? ShellResult, int? ShellState)
{
    public override string ToString() =>
        $"lastPause={Pause} inputDesktop={InputDesktop?.ToString() ?? "not-checked"} " +
        $"shellResult={ShellResult?.ToString() ?? "not-checked"} shellState={ShellState?.ToString() ?? "not-checked"}";
}
