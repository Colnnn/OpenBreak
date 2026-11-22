using Microsoft.Data.Sqlite;

namespace OpenBreak;

internal static class CapturePreferenceChecks
{
    internal static void Run(string folder)
    {
        ValidateChords();
        CheckRegistration();
        CheckStoreChanges(Path.Combine(folder, "capture-preferences.db"));
        CheckStartupFallback(Path.Combine(folder, "capture-load-failure.db"));
    }

    private static void ValidateChords()
    {
        Require(Parse(" shift + ctrl + k ").ToString() == "Ctrl+Shift+K" &&
            Parse("Shift+Alt+Ctrl+F11").ToString() == "Ctrl+Alt+Shift+F11" &&
            Parse("Alt+Shift+0").ToString() == "Alt+Shift+0" &&
            Parse("Ctrl+Alt+K") == CaptureShortcut.Default, "supported chords are rendered canonically");
        var invalid = new[] { "K", "Ctrl+K", "Ctrl+Ctrl+K", "Win+Alt+K", "Ctrl+Alt+F12", "Ctrl+Alt+F01", "Ctrl+Alt+Delete", "Ctrl+Alt+", "Ctrl+Alt+Shift+Win+A" };
        Require(invalid.All(text => !CaptureShortcut.TryParse(text, out _)), "bare, duplicate, reserved and unsupported keys are rejected");
        Console.WriteLine("PASS supported and invalid capture shortcut input");
    }

    private static void CheckRegistration()
    {
        var native = new NativeBindings { Now = uint.MaxValue - 8L };
        using var binding = native.Create();
        var old = CaptureShortcut.Default;
        var next = Parse("Ctrl+Shift+F2");
        Require(binding.TryChange(old, static () => { }), "default registration succeeds");
        var oldId = native.Registered.Single().Key;
        var stagedId = 0;
        var queuedAt = 0u;
        Require(binding.TryChange(next, () =>
        {
            native.Now += 5;
            stagedId = native.Registered.Single(pair => pair.Value == next).Key;
            queuedAt = unchecked((uint)native.Now);
            Require(stagedId != oldId && Matches(binding, oldId, old, queuedAt) && !Matches(binding, stagedId, next, queuedAt),
                "the previous chord remains active while the new chord is staged");
            native.Now += 5;
        }), "the staged chord promotes after the save");

        native.Now += 16;
        var currentTime = unchecked((uint)native.Now);
        Require(!Matches(binding, stagedId, next, queuedAt) && !Matches(binding, oldId, old, currentTime) &&
            !Matches(binding, stagedId, old, currentTime) && Matches(binding, stagedId, next, currentTime),
            "only the new ID and chord with a post-promotion timestamp are accepted across uptime wrap");
        var beforeRebind = stagedId;
        native.FailRelease = true;
        Require(binding.TryChange(old, static () => { }) && binding.HasUnreleasedBindings &&
            !Matches(binding, beforeRebind, next, currentTime) && !Matches(binding, oldId, old, currentTime),
            "unreleased retired chords and stale IDs from an earlier use of the same chord cannot dispatch");
        native.FailRelease = false;
        Require(binding.TryChange(next, static () => { }) && native.Registered.Count == 1 && !binding.HasUnreleasedBindings,
            "the next apply retries releasing retired native registrations");

        native.FailRelease = true;
        try
        {
            binding.TryChange(old, () => throw new IOException("Synthetic write failure."));
            throw new InvalidOperationException("Expected the staged registration to fail persistence.");
        }
        catch (IOException)
        {
            var rolledBack = native.Registered.Single(pair => pair.Value == old).Key;
            Require(binding.Active == next && binding.HasUnreleasedBindings && !Matches(binding, rolledBack, old, currentTime),
                "a rolled-back chord remains inactive even if Windows has not released it yet");
        }
        native.FailRelease = false;
        Require(binding.TryChange(old, static () => { }) && native.Registered.Count == 1 && !binding.HasUnreleasedBindings,
            "a formerly rolled-back chord can be registered after release recovers");
        binding.Dispose();
        Require(native.Registered.Count == 0 && binding.Active is null, "dispose releases all owned native registrations");

        var rejected = new NativeBindings { RejectAll = true };
        using var exhausted = rejected.Create();
        for (var index = 0; index <= 0xbfff; index++)
        {
            Require(!exhausted.TryChange(old, () => throw new InvalidOperationException("Cannot save an unregistered shortcut.")),
                "application IDs exhaust without wrapping or persisting");
        }
        Require(rejected.LastAttempt == 0xbfff, "all attempted IDs are within the Windows app registration range");
        Console.WriteLine("PASS staged, pending, stale and retired hotkey filtering, release retry and ID exhaustion");
    }

    private static void CheckStoreChanges(string path)
    {
        var store = new TaskStore(path);
        var native = new NativeBindings();
        using var binding = native.Create();
        var options = new CaptureOptions(store, binding);
        options.Load();
        var old = CaptureShortcut.Default;
        var next = Parse("Ctrl+Shift+J");
        var oldRegistration = native.Registered.Single();
        native.Unavailable.Add(next);
        Require(options.Apply(next) is not null && binding.Active == old && native.Registered.Single().Equals(oldRegistration) &&
            store.LoadCapturePreferences().Shortcut == old, "a native conflict preserves the working and saved shortcut");
        native.Unavailable.Clear();
        using (var blocker = Open(path))
        {
            using var transaction = blocker.BeginTransaction();
            Require(options.Apply(next) is not null && binding.Active == old && native.Registered.Single().Equals(oldRegistration),
                "a SQLite write lock rolls back the staged shortcut without replacing the old registration");
            transaction.Rollback();
        }
        Require(store.LoadCapturePreferences().Shortcut == old, "the locked save leaves the previous durable chord");
        Require(options.DismissGuide() && options.Apply(next) is null && binding.Active == next && native.Registered.Count == 1 &&
            new TaskStore(path).LoadCapturePreferences() == new CapturePreferences(next, true),
            "successful retry persists a new chord alongside the existing guidance acknowledgment");
        Console.WriteLine("PASS registration conflict and actual SQLite write failure preserve the original shortcut; retry persists");
    }

    private static void CheckStartupFallback(string path)
    {
        var store = new TaskStore(path);
        var saved = Parse("Alt+Shift+F3");
        store.SaveCaptureShortcut(saved);
        var native = new NativeBindings();
        native.Unavailable.Add(saved);
        using var binding = native.Create();
        var options = new CaptureOptions(store, binding);
        options.Load();
        Require(binding.Active is null && options.Preferences.Shortcut == saved && options.Status.Contains("unavailable"),
            "a conflicting saved chord leaves tray capture available with actionable status");

        var loadFailure = new CaptureOptions(store, binding);
        using (var blocker = Open(path))
        using (var command = blocker.CreateCommand())
        {
            command.CommandText = "BEGIN EXCLUSIVE";
            command.ExecuteNonQuery();
            loadFailure.Load();
            command.CommandText = "ROLLBACK";
            command.ExecuteNonQuery();
        }
        Require(loadFailure.LoadFailed && binding.Active is null && loadFailure.DismissGuide() &&
            store.LoadCapturePreferences() == new CapturePreferences(saved, true),
            "acknowledging guidance after a failed startup read never writes the fallback shortcut");
        native.Unavailable.Clear();
        loadFailure.Load();
        Require(!loadFailure.LoadFailed && binding.Active == saved && loadFailure.Preferences.GuideDismissed,
            "reopening settings recovers the stored shortcut and guidance state");
        Console.WriteLine("PASS saved-shortcut conflict, startup read failure recovery and independent guidance persistence");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static bool Matches(CaptureBinding binding, int id, CaptureShortcut shortcut, uint time) =>
        binding.Matches((nuint)id, shortcut.Modifiers, shortcut.Key, time);

    private static CaptureShortcut Parse(string text) => CaptureShortcut.TryParse(text, out var shortcut)
        ? shortcut : throw new InvalidOperationException($"Invalid shortcut fixture: {text}");

    private static void Require(bool passed, string message)
    {
        if (!passed)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class NativeBindings
    {
        internal Dictionary<int, CaptureShortcut> Registered { get; } = [];
        internal HashSet<CaptureShortcut> Unavailable { get; } = [];
        internal long Now { get; set; } = 100;
        internal bool FailRelease { get; set; }
        internal bool RejectAll { get; init; }
        internal int LastAttempt { get; private set; }

        internal CaptureBinding Create() => new(Register, id => !FailRelease && Registered.Remove(id), () => Now);

        private bool Register(int id, CaptureShortcut shortcut)
        {
            LastAttempt = id;
            return !RejectAll && !Unavailable.Contains(shortcut) && !Registered.ContainsValue(shortcut) && Registered.TryAdd(id, shortcut);
        }
    }
}
