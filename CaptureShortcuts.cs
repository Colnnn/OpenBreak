using System.Globalization;

namespace OpenBreak;

internal readonly record struct CaptureShortcut(uint Modifiers, uint Key)
{
    private const uint Alt = 1;
    private const uint Control = 2;
    private const uint Shift = 4;
    private const uint FirstFunctionKey = 0x70;

    internal static CaptureShortcut Default => new(Control | Alt, 'K');

    internal static bool TryParse(string text, out CaptureShortcut shortcut)
    {
        shortcut = default;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        uint modifiers = 0;
        foreach (var part in parts.AsSpan(0, parts.Length - 1))
        {
            uint flag = part.ToUpperInvariant() switch
            {
                "CTRL" => Control,
                "ALT" => Alt,
                "SHIFT" => Shift,
                _ => 0,
            };
            if (flag == 0 || (modifiers & flag) != 0)
            {
                return false;
            }
            modifiers |= flag;
        }

        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && key[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            shortcut = new CaptureShortcut(modifiers, key[0]);
        }
        else if (key.Length is 2 or 3 && key[0] == 'F' &&
            int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number is >= 1 and <= 11 && key == FormattableString.Invariant($"F{number}"))
        {
            shortcut = new CaptureShortcut(modifiers, FirstFunctionKey + (uint)number - 1);
        }
        else
        {
            return false;
        }
        return true;
    }

    public override string ToString() =>
        ((Modifiers & Control) != 0 ? "Ctrl+" : "") +
        ((Modifiers & Alt) != 0 ? "Alt+" : "") +
        ((Modifiers & Shift) != 0 ? "Shift+" : "") +
        (Key >= FirstFunctionKey ? FormattableString.Invariant($"F{Key - FirstFunctionKey + 1}") : ((char)Key).ToString());
}

internal sealed class CaptureBinding(Func<int, CaptureShortcut, bool> register, Func<int, bool> unregister, Func<long>? uptime = null)
    : IDisposable
{
    private readonly Func<long> uptime = uptime ?? (() => Environment.TickCount64);
    private readonly HashSet<int> owned = [];
    private (int Id, CaptureShortcut Shortcut, long ActivatedAt)? active;
    private int nextId;
    private bool disposed;

    internal CaptureShortcut? Active => active?.Shortcut;
    internal bool HasUnreleasedBindings => owned.Count > (active.HasValue ? 1 : 0);

    internal bool TryChange(CaptureShortcut shortcut, Action persist)
    {
        if (disposed)
        {
            return false;
        }
        foreach (var retired in owned.Where(id => id != active?.Id).ToArray())
        {
            Release(retired);
        }
        if (active?.Shortcut == shortcut)
        {
            persist();
            return true;
        }

        if (nextId == 0xbfff)
        {
            return false;
        }

        var id = ++nextId;
        if (!register(id, shortcut))
        {
            return false;
        }
        owned.Add(id);
        try
        {
            persist();
        }
        catch
        {
            Release(id);
            throw;
        }

        var previous = active;
        active = (id, shortcut, uptime());
        if (previous is { } prior)
        {
            Release(prior.Id);
        }
        return true;
    }

    internal bool Matches(nuint id, uint modifiers, uint key, uint messageTime)
    {
        if (active is not { } current || id != (nuint)current.Id || current.Shortcut != new CaptureShortcut(modifiers, key))
        {
            return false;
        }

        // WM_HOTKEY carries a wrapping 32-bit uptime timestamp via GetMessageTime.
        var now = uptime();
        var age = unchecked((uint)now - messageTime);
        return now - age > current.ActivatedAt;
    }

    public void Dispose()
    {
        disposed = true;
        active = null;
        foreach (var id in owned.ToArray())
        {
            Release(id);
        }
    }

    private void Release(int id)
    {
        if (unregister(id))
        {
            owned.Remove(id);
        }
    }
}

internal sealed record CapturePreferences(CaptureShortcut Shortcut, bool GuideDismissed);

internal sealed class CaptureOptions(TaskStore store, CaptureBinding binding)
{
    internal CapturePreferences Preferences { get; private set; } = new(CaptureShortcut.Default, false);
    internal bool LoadFailed { get; private set; }

    internal string Status
    {
        get
        {
            var status = LoadFailed
                ? "Couldn’t load the saved capture settings. You can capture from the tray. Reopen settings to retry."
                : binding.Active is { } active && active == Preferences.Shortcut
                    ? $"Shortcut active · {active}"
                    : $"{Preferences.Shortcut} is unavailable here. Choose another shortcut or use Capture task in the tray.";
            return binding.HasUnreleasedBindings
                ? $"{status} Windows couldn’t release a previous shortcut. Quit OpenBreak to release it."
                : status;
        }
    }

    internal void Load()
    {
        try
        {
            Preferences = store.LoadCapturePreferences();
            LoadFailed = false;
            binding.TryChange(Preferences.Shortcut, static () => { });
        }
        catch (Exception error) when (TaskStore.IsStorageError(error))
        {
            LoadFailed = true;
        }
    }

    internal string? Apply(CaptureShortcut shortcut)
    {
        // When a failed read has recovered, retain the separately saved guide preference.
        if (LoadFailed)
        {
            Load();
            if (LoadFailed)
            {
                return "Couldn’t read the saved capture settings. Check the database and try again.";
            }
        }

        try
        {
            if (!binding.TryChange(shortcut, () => store.SaveCaptureShortcut(shortcut)))
            {
                return $"Windows couldn’t register {shortcut}. Choose another shortcut or use Capture task in the tray.";
            }
        }
        catch (Exception error) when (TaskStore.IsStorageError(error))
        {
            return "Couldn’t save the shortcut. Capture task is still available from the tray.";
        }

        Preferences = Preferences with { Shortcut = shortcut };
        return null;
    }

    internal bool DismissGuide()
    {
        if (Preferences.GuideDismissed)
        {
            return true;
        }
        try
        {
            store.DismissCaptureGuide();
            Preferences = Preferences with { GuideDismissed = true };
            return true;
        }
        catch (Exception error) when (TaskStore.IsStorageError(error))
        {
            return false;
        }
    }
}
