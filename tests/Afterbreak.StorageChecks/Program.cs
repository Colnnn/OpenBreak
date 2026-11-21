using Microsoft.Data.Sqlite;

namespace Afterbreak;

internal static class Program
{
    private static async Task Main()
    {
        var temporary = Directory.CreateTempSubdirectory("Afterbreak-storage-");
        try
        {
            RoundTrip(Path.Combine(temporary.FullName, "roundtrip.db"));
            Console.WriteLine("PASS task edits, status moves, ordering, archive and restore across reopen");
            PreserveUnsupportedFiles(temporary.FullName);
            Console.WriteLine("PASS existing empty, corrupt, foreign and future-schema files remain unchanged");
            PreserveFailedWrites(Path.Combine(temporary.FullName, "writes.db"));
            Console.WriteLine("PASS rejected inserts and edits preserve data; partial reordering rolls back");
            await PublishAtomically(Path.Combine(temporary.FullName, "parallel"));
            Console.WriteLine("PASS concurrent first writes and abandoned initialization file");
            CheckReminderMigration(temporary.FullName);
            Console.WriteLine("PASS schema-one migration, physical corruption and failed migration preservation");
            CheckReminderWriteFailure(Path.Combine(temporary.FullName, "reminder-writes.db"));
            Console.WriteLine("PASS reminder settings and checkpoint commit together and roll back together");
            CheckCaptureMigration(temporary.FullName);
            Console.WriteLine("PASS capture preferences migration preserves v1/v2 tasks and existing reminder state");
            CapturePreferenceChecks.Run(temporary.FullName);
            CheckAppearancePolicy();
            Console.WriteLine("PASS appearance policy for theme, contrast, transparency, motion and text scaling");
            CheckAppearanceMigration(temporary.FullName);
            Console.WriteLine("PASS appearance upgrade and rollback preserve task order, archive, shortcut, guide and reminder records");
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static void RoundTrip(string path)
    {
        var store = new TaskStore(path);
        Require(store.ReturnTask() is null, "The empty board has no return task.");
        store.Add("  Draft the notes  ");
        store.Add("Check totals, revision 'B'");
        store.Add("Send both drawings");
        var initial = store.List(archived: false);
        var notes = initial[0].Id;
        var totals = initial[1].Id;
        var drawings = initial[2].Id;
        AssertIds(initial, notes, totals, drawings);
        Require(initial[0].Title == "Draft the notes", "Capture trims the title.");
        Require(store.ReturnTask()?.Id == notes, "Return task falls back to the first To do card.");

        store.Edit(totals, "Confirm revised totals", " Compare café prices; recheck page two. ");
        store.Move(drawings, TaskStatus.InProgress);
        store.Move(totals, TaskStatus.InProgress, beforeId: drawings);
        Require(store.ReturnTask()?.Id == totals, "The first ordered In progress task leads the break.");
        store.MoveWithin(drawings, -1);
        var reordered = new TaskStore(path);
        AssertIds(reordered.List(archived: false), notes, drawings, totals);
        Require(reordered.ReturnTask()?.Id == drawings, "The changed order survives reopening and chooses the return task.");
        store.Move(drawings, TaskStatus.Done);
        store.SetArchived(totals, archived: true);

        var reopened = new TaskStore(path);
        AssertIds(reopened.List(archived: false), notes, drawings);
        var archived = reopened.List(archived: true).Single();
        Require(archived == new BoardTask(totals, "Confirm revised totals", "Compare café prices; recheck page two.", TaskStatus.InProgress),
            "The archived title, note and original status survive reopening.");
        Require(reopened.ReturnTask()?.Id == notes, "Archived In progress cards are ignored.");
        reopened.SetArchived(totals, archived: false);
        reopened.Edit(totals, archived.Title, "  ");
        Require(reopened.ReturnTask() is { Id: var id, NextStep: null } && id == totals, "Restore returns to the original status and blank note clears.");
        reopened.SetArchived(totals, archived: true);
        reopened.Move(notes, TaskStatus.Done);
        Require(reopened.ReturnTask() is null, "Done and archived cards leave a timer-only break.");
    }

    private static void PreserveUnsupportedFiles(string folder)
    {
        var future = Path.Combine(folder, "future.db");
        new TaskStore(future).Add("Keep this record");
        ExecuteSql(future, "PRAGMA user_version = 9000;");
        AssertRejectedUnchanged(future);

        var foreign = Path.Combine(folder, "foreign.db");
        ExecuteSql(foreign, "CREATE TABLE unrelated (message TEXT); INSERT INTO unrelated VALUES ('keep');");
        AssertRejectedUnchanged(foreign);

        var malformed = Path.Combine(folder, "invalid.db");
        File.WriteAllText(malformed, "This file is not a database.");
        AssertRejectedUnchanged(malformed);

        var empty = Path.Combine(folder, "empty.db");
        File.WriteAllBytes(empty, []);
        AssertRejectedUnchanged(empty);
    }

    private static void AssertRejectedUnchanged(string path)
    {
        var before = File.ReadAllBytes(path);
        var store = new TaskStore(path);
        ExpectStorageError(() => store.List(archived: false));
        ExpectStorageError(() => store.Add("Do not overwrite"));
        Require(File.ReadAllBytes(path).SequenceEqual(before), "Opening and writing must both preserve unsupported files.");
    }

    private static void PreserveFailedWrites(string path)
    {
        var store = new TaskStore(path);
        store.Add("First");
        store.Add("Second");
        store.Add("Third");
        var initial = store.List(archived: false);
        ExecuteSql(path, """
            CREATE TRIGGER reject_capture BEFORE INSERT ON tasks BEGIN SELECT RAISE(ABORT, 'capture failure'); END;
            CREATE TRIGGER reject_edit BEFORE UPDATE OF title ON tasks BEGIN SELECT RAISE(ABORT, 'edit failure'); END;
            CREATE TRIGGER reject_reorder BEFORE UPDATE OF position ON tasks
                WHEN OLD.id = 1 AND OLD.position <> NEW.position BEGIN SELECT RAISE(ABORT, 'move failure'); END;
            """);
        ExpectStorageError(() => store.Add("This capture fails"));
        ExpectStorageError(() => store.Edit(initial[0].Id, "This edit fails", "Draft note"));
        ExpectStorageError(() => store.Move(initial[2].Id, TaskStatus.ToDo, beforeId: initial[0].Id));
        Require(new TaskStore(path).List(archived: false).SequenceEqual(initial), "Failed writes must keep the original rows and their order.");
        ExecuteSql(path, "DROP TRIGGER reject_capture; DROP TRIGGER reject_edit; DROP TRIGGER reject_reorder;");
        store.Add("Retry capture");
        Require(store.List(archived: false).Count == 4, "A later capture can succeed after a write failure.");
    }

    private static async Task PublishAtomically(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "tasks.db");
        var abandoned = Path.Combine(folder, ".tasks-abandoned.tmp");
        File.WriteAllBytes(abandoned, []);
        using var start = new Barrier(2);
        Task Save(string title) => Task.Run(() =>
        {
            Require(start.SignalAndWait(TimeSpan.FromSeconds(10)), "Both first writers must reach the start barrier.");
            new TaskStore(path).Add(title);
        });

        await Task.WhenAll(Save("First writer"), Save("Second writer"));
        var titles = new TaskStore(path).List(archived: false).Select(task => task.Title).Order().ToArray();
        Require(titles.SequenceEqual(["First writer", "Second writer"]), "Concurrent initialization must publish one database with both writes.");
        Require(Directory.GetFiles(folder, "*.tmp").SequenceEqual([abandoned]), "Retries ignore stale artifacts and clean up their own.");
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CheckReminderMigration(string folder)
    {
        var path = Path.Combine(folder, "migration.db");
        CreateSchemaOne(path);
        var before = TaskFingerprint(path);
        var store = new TaskStore(path);
        var loaded = store.LoadReminders();
        Require(loaded.Settings == ReminderSettings.Default && loaded.Checkpoint is null,
            "Migration never opts into reminders.");
        Require(Scalar(path, "PRAGMA user_version") is 4L && TaskFingerprint(path) == before,
            "Migration preserves each task ID, title, next step, lane order and archive flag.");

        var blocked = Path.Combine(folder, "blocked-migration.db");
        CreateSchemaOne(blocked);
        ExecuteSql(blocked, "CREATE TABLE reminder_state (contents TEXT); INSERT INTO reminder_state VALUES ('keep');");
        var bytes = File.ReadAllBytes(blocked);
        ExpectStorageError(() => new TaskStore(blocked).LoadReminders());
        Require(File.ReadAllBytes(blocked).SequenceEqual(bytes) && Scalar(blocked, "PRAGMA user_version") is 1L,
            "An incompatible schema-one change rolls back without replacing any data.");

        var corrupt = Path.Combine(folder, "corrupt-migration.db");
        CreateSchemaOne(corrupt);
        var page = (long)Scalar(corrupt, "SELECT rootpage FROM sqlite_schema WHERE name = 'tasks'")!;
        var size = (long)Scalar(corrupt, "PRAGMA page_size")!;
        using (var file = File.OpenWrite(corrupt))
        {
            file.Position = (page - 1) * size;
            file.WriteByte(0);
        }
        bytes = File.ReadAllBytes(corrupt);
        ExpectStorageError(() => new TaskStore(corrupt).LoadReminders());
        Require(File.ReadAllBytes(corrupt).SequenceEqual(bytes), "Migration must not write to a corrupt task page.");
    }

    private static void CreateSchemaOne(string path) => ExecuteSql(path, $"""
        CREATE TABLE tasks (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 140),
            next_step TEXT CHECK (length(next_step) <= 480),
            status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 2),
            position INTEGER NOT NULL CHECK (position >= 0),
            archived INTEGER NOT NULL CHECK (archived IN (0, 1))
        ) STRICT;
        CREATE INDEX task_order ON tasks (archived, status, position, id);
        PRAGMA application_id = {0x41465442};
        PRAGMA user_version = 1;
        INSERT INTO tasks VALUES (1, 'Last task', 'Read the notes', 0, 2, 0), (2, 'First task', NULL, 0, 1, 0),
            (3, 'In progress', 'Continue at page three', 1, 0, 0), (4, 'Already done', NULL, 2, 0, 0),
            (5, 'Archived task', 'Keep original status', 1, 0, 1);
        """);

    private static string? TaskFingerprint(string path) => Scalar(path, """
        SELECT group_concat(id || ':' || title || ':' || coalesce(next_step, '') || ':' || status || ':' || position || ':' || archived)
        FROM (SELECT * FROM tasks ORDER BY archived, status, position, id)
        """) as string;

    private static void CheckReminderWriteFailure(string path)
    {
        var store = new TaskStore(path);
        store.Add("Unaffected task");
        var initialTasks = TaskFingerprint(path);
        var settings = ReminderSettings.Default with { Enabled = true, WorkSeconds = 60, WorkingHours = true, TimerOnly = true };
        var checkpoint = new ReminderCheckpoint(ReminderPhase.Work, TimeSpan.FromSeconds(50), 2, true,
            TimeSpan.FromSeconds(2), new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        store.SaveReminders(settings, checkpoint);
        Require(new TaskStore(path).LoadReminders() == (settings, checkpoint), "Reminder settings and checkpoint survive reopening.");
        ExecuteSql(path, """
            CREATE TRIGGER reject_reminders BEFORE UPDATE ON reminder_state BEGIN SELECT RAISE(ABORT, 'write failure'); END;
            """);
        ExpectStorageError(() => store.SaveReminders(settings with { Enabled = false }, checkpoint with { CompletedShorts = 0 }));
        Require(new TaskStore(path).LoadReminders() == (settings, checkpoint) && TaskFingerprint(path) == initialTasks,
            "A settings write failure preserves the prior configuration, checkpoint, and tasks.");
    }

    private static void CheckCaptureMigration(string folder)
    {
        var path = Path.Combine(folder, "schema-v2.db");
        CreateSchemaOne(path);
        var fingerprint = TaskFingerprint(path);
        var reminders = ReminderSettings.Default with { Enabled = true, WorkSeconds = 33, TimerOnly = true };
        var checkpoint = new ReminderCheckpoint(ReminderPhase.Snoozed, TimeSpan.FromMinutes(2), 3, true,
            TimeSpan.FromSeconds(7), new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        new TaskStore(path).SaveReminders(reminders, checkpoint);
        ExecuteSql(path, "DROP TABLE appearance_preferences; DROP TABLE capture_preferences; PRAGMA user_version = 2;");
        var savedReminderRow = Scalar(path, "SELECT settings || checkpoint FROM reminder_state WHERE id = 1");
        var migrated = new TaskStore(path);
        Require(migrated.LoadCapturePreferences() == new CapturePreferences(CaptureShortcut.Default, false),
            "Existing installations start with the default capture shortcut and optional guide.");
        Require(Scalar(path, "PRAGMA user_version") is 4L && TaskFingerprint(path) == fingerprint &&
            Equals(savedReminderRow, Scalar(path, "SELECT settings || checkpoint FROM reminder_state WHERE id = 1")),
            "Migration leaves the existing task and reminder rows byte-identical.");
        Require(CaptureShortcut.TryParse("Ctrl+Shift+F5", out var shortcut), "A test shortcut can be parsed.");
        migrated.SaveCaptureShortcut(shortcut);
        migrated.DismissCaptureGuide();
        Require(TaskFingerprint(path) == fingerprint &&
            Equals(savedReminderRow, Scalar(path, "SELECT settings || checkpoint FROM reminder_state WHERE id = 1")),
            "Changing either capture preference leaves the existing task and reminder rows byte-identical.");

        var blocked = Path.Combine(folder, "capture-blocked-v1.db");
        CreateSchemaOne(blocked);
        ExecuteSql(blocked, "CREATE TABLE capture_preferences (keep TEXT); INSERT INTO capture_preferences VALUES ('original');");
        var bytes = File.ReadAllBytes(blocked);
        ExpectStorageError(() => new TaskStore(blocked).LoadCapturePreferences());
        Require(File.ReadAllBytes(blocked).SequenceEqual(bytes) && Scalar(blocked, "PRAGMA user_version") is 1L &&
            Scalar(blocked, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'reminder_state'") is 0L,
            "A failed v1-to-v3 upgrade rolls back the intermediate reminder DDL.");
    }

    private static void CheckAppearancePolicy()
    {
        var system = new AppearanceSystem(false, true, true, 1, 0xFFFFFF, 0x000000);
        var state = AppearancePolicy.Resolve(AppearancePreferences.Default, system);
        Require(state.Theme == AppearanceTheme.System && state.Scene == BreakScene.Dusk && state.ShowScene &&
            state.UseMaterials && state.AnimateScene && state.ScaleWidth(460) == 460 && state.ScaleHeight(165) == 165,
            "default follows Windows and shows the dusk scene with allowed effects");
        var choices = new AppearancePreferences(AppearanceTheme.Dark, BreakScene.Daylight);
        state = AppearancePolicy.Resolve(choices, system with { Animations = false, TextScale = 2 });
        Require(state.Theme == AppearanceTheme.Dark && state.Scene == BreakScene.Daylight && !state.AnimateScene && state.UseMaterials &&
            state.ScaleWidth(460) == 620 && state.ScaleHeight(165) == 380, "reduced motion and text scale do not override the scene or material");

        state = AppearancePolicy.Resolve(choices, system with { Transparency = false });
        Require(!state.UseMaterials && state.ShowScene && state.AnimateScene, "transparency off leaves an opaque still scene with separately allowed entrance motion");
        foreach (var palette in new[] { (Window: 0x030615u, Text: 0xFCFFFFu), (Window: 0xFFFFD1u, Text: 0x083421u) })
        {
            state = AppearancePolicy.Resolve(choices, system with { HighContrast = true, WindowColor = palette.Window, TextColor = palette.Text });
            Require(state.Theme == AppearanceTheme.System && !state.ShowScene && !state.AnimateScene && !state.UseMaterials &&
                state.WindowColor == palette.Window && state.TextColor == palette.Text, "contrast overrides app theme and uses either supplied system palette");
        }
        state = AppearancePolicy.Resolve(choices, system with { TextScale = double.NaN });
        Require(state.TextScale == 1, "an unavailable scale uses normal dimensions");
    }

    private static void CheckAppearanceMigration(string folder)
    {
        var path = Path.Combine(folder, "appearance-v3.db");
        CreateSchemaOne(path);
        var store = new TaskStore(path);
        var settings = ReminderSettings.Default with { Enabled = true, ShortSeconds = 9 };
        var checkpoint = new ReminderCheckpoint(ReminderPhase.Snoozed, TimeSpan.FromMinutes(2), 2, false,
            TimeSpan.Zero, new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        store.SaveReminders(settings, checkpoint);
        Require(CaptureShortcut.TryParse("Alt+Shift+J", out var shortcut), "valid preference shortcut fixture");
        store.SaveCaptureShortcut(shortcut);
        store.DismissCaptureGuide();
        ExecuteSql(path, "DROP TABLE appearance_preferences; PRAGMA user_version = 3;");
        var tasks = TaskFingerprint(path);
        var capture = Scalar(path, "SELECT shortcut || guide_dismissed FROM capture_preferences WHERE id = 1");
        var reminder = Scalar(path, "SELECT settings || checkpoint FROM reminder_state WHERE id = 1");

        Require(store.LoadAppearance() == AppearancePreferences.Default && Scalar(path, "PRAGMA user_version") is 4L,
            "v3 upgrade defaults to System/Dusk");
        var choice = new AppearancePreferences(AppearanceTheme.Light, BreakScene.Daylight);
        store.SaveAppearance(choice);
        Require(new TaskStore(path).LoadAppearance() == choice && TaskFingerprint(path) == tasks &&
            Equals(capture, Scalar(path, "SELECT shortcut || guide_dismissed FROM capture_preferences WHERE id = 1")) &&
            Equals(reminder, Scalar(path, "SELECT settings || checkpoint FROM reminder_state WHERE id = 1")),
            "saving appearance leaves all existing task and preference rows unchanged");
        ExecuteSql(path, "CREATE TRIGGER reject_appearance BEFORE UPDATE ON appearance_preferences BEGIN SELECT RAISE(ABORT, 'write failure'); END;");
        ExpectStorageError(() => store.SaveAppearance(new AppearancePreferences(AppearanceTheme.Dark, BreakScene.Dusk)));
        Require(new TaskStore(path).LoadAppearance() == choice, "a failed write preserves the complete prior appearance pair");

        var blocked = Path.Combine(folder, "appearance-conflict.db");
        CreateSchemaOne(blocked);
        ExecuteSql(blocked, "CREATE TABLE appearance_preferences (keep TEXT); INSERT INTO appearance_preferences VALUES ('original');");
        var bytes = File.ReadAllBytes(blocked);
        ExpectStorageError(() => new TaskStore(blocked).LoadAppearance());
        Require(File.ReadAllBytes(blocked).SequenceEqual(bytes) && Scalar(blocked, "PRAGMA user_version") is 1L &&
            Scalar(blocked, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('capture_preferences', 'reminder_state')") is 0L,
            "a failed latest-schema upgrade rolls back intermediate preference DDL");
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void ExpectStorageError(Action attempt)
    {
        try
        {
            attempt();
        }
        catch (Exception error) when (TaskStore.IsStorageError(error))
        {
            return;
        }
        throw new InvalidOperationException("Expected the storage operation to be rejected.");
    }

    private static void AssertIds(IReadOnlyList<BoardTask> tasks, params long[] ids) =>
        Require(tasks.Select(task => task.Id).SequenceEqual(ids), "Task IDs must be in the expected order.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
