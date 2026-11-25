using Microsoft.Data.Sqlite;

namespace OpenBreak;

internal enum TaskStatus
{
    ToDo = 0,
    InProgress = 1,
    Done = 2,
}

internal sealed record BoardTask(long Id, string Title, string? NextStep, TaskStatus Status);

internal sealed partial class TaskStore(string path)
{
    internal const int TitleLimit = 140;
    internal const int NextStepLimit = 480;
    // Keep the original on-disk identity so existing tasks and settings open in place.
    private const string UserDataDirectory = "Afterbreak";
    private const int ApplicationId = 0x41465442;
    private const int SchemaVersion = 11;
    private const string TaskFields = "id, title, next_step, status";

    internal static string CurrentUserDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), UserDataDirectory, "tasks.db");

    internal static TaskStore ForCurrentUser() => new(CurrentUserDatabasePath);

    internal static bool IsStorageError(Exception error) =>
        error is SqliteException or InvalidDataException or IOException or UnauthorizedAccessException;

    internal IReadOnlyList<BoardTask> List(bool archived)
    {
        using var connection = Open();
        using var command = Command(connection, null, $"""
            SELECT {TaskFields} FROM tasks WHERE archived = $archived ORDER BY status, position, id
            """, ("$archived", archived));
        using var reader = command.ExecuteReader();
        var tasks = new List<BoardTask>();
        while (reader.Read())
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    internal BoardTask? ReturnTask()
    {
        using var connection = Open();
        using var command = Command(connection, null, $"""
            SELECT {TaskFields} FROM tasks WHERE archived = 0 AND status IN ($todo, $progress)
            ORDER BY (status = $progress) DESC, position, id LIMIT 1
            """, ("$todo", (int)TaskStatus.ToDo), ("$progress", (int)TaskStatus.InProgress));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    internal void Add(string title, string? nextStep = null)
    {
        title = NormalizeTitle(title);
        nextStep = NormalizeNextStep(nextStep);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var position = NextPosition(connection, transaction, TaskStatus.ToDo, archived: false);
        using var insert = Command(connection, transaction, """
            INSERT INTO tasks (title, next_step, status, position, archived) VALUES ($title, $note, $status, $position, 0)
            """, ("$title", title), ("$note", (object?)nextStep ?? DBNull.Value), ("$status", (int)TaskStatus.ToDo),
            ("$position", position));
        EnsureTaskChanged(insert);
        transaction.Commit();
    }

    internal void Edit(long id, string title, string? nextStep)
    {
        title = NormalizeTitle(title);
        nextStep = NormalizeNextStep(nextStep);

        using var connection = Open();
        using var update = Command(connection, null, """
            UPDATE tasks SET title = $title, next_step = $note WHERE id = $id AND archived = 0
            """, ("$title", title), ("$note", (object?)nextStep ?? DBNull.Value), ("$id", id));
        EnsureTaskChanged(update);
    }

    internal void Move(long id, TaskStatus destination, long? beforeId = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var source = GetStatus(connection, transaction, id, archived: false);
        var ids = ActiveIds(connection, transaction, destination);
        if (source == destination && beforeId == id)
        {
            return;
        }

        ids.Remove(id);
        var index = beforeId is long next ? ids.IndexOf(next) : ids.Count;
        if (index < 0)
        {
            throw new InvalidDataException("The destination task no longer exists in this column.");
        }

        ids.Insert(index, id);
        SaveOrder(connection, transaction, destination, ids);
        transaction.Commit();
    }

    internal void MoveWithin(long id, int offset)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var status = GetStatus(connection, transaction, id, archived: false);
        var ids = ActiveIds(connection, transaction, status);
        var from = ids.IndexOf(id);
        var to = from + offset;
        if (to < 0 || to >= ids.Count)
        {
            return;
        }

        ids.RemoveAt(from);
        ids.Insert(to, id);
        SaveOrder(connection, transaction, status, ids);
        transaction.Commit();
    }

    internal void SetArchived(long id, bool archived)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var status = GetStatus(connection, transaction, id, archived: !archived);
        var position = NextPosition(connection, transaction, status, archived);
        using var update = Command(connection, transaction, """
            UPDATE tasks SET archived = $archived, position = $position WHERE id = $id
            """, ("$archived", archived), ("$position", position), ("$id", id));
        EnsureTaskChanged(update);
        transaction.Commit();
    }

    internal void DeleteArchived(long id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var delete = Command(connection, transaction, "DELETE FROM tasks WHERE id = $id AND archived = 1", ("$id", id));
        EnsureTaskChanged(delete);
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        if (!File.Exists(path))
        {
            CreateDatabase();
        }

        var connection = Connect(path);
        try
        {
            RequireCurrentSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void CreateDatabase()
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".tasks-{Guid.NewGuid():N}.tmp");
        using (File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }
        try
        {
            using (var connection = Connect(temporary))
            {
                Initialize(connection);
            }
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another connection published a database first. Open validates the published file.
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static SqliteConnection Connect(string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 1,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var create = Command(connection, transaction, $"""
            CREATE TABLE tasks (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND {TitleLimit}),
                next_step TEXT CHECK (length(next_step) <= {NextStepLimit}),
                status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 2),
                position INTEGER NOT NULL CHECK (position >= 0),
                archived INTEGER NOT NULL CHECK (archived IN (0, 1))
            ) STRICT;
            CREATE INDEX task_order ON tasks (archived, status, position, id);
            {ReminderTable}
            {CaptureTable}
            {AppearanceTable}
            {FloatingTimerPlacementTable}
            PRAGMA application_id = {ApplicationId};
            PRAGMA user_version = {SchemaVersion};
            """);
        create.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void RequireCurrentSchema(SqliteConnection connection)
    {
        if (ReadPragma(connection, null, "PRAGMA application_id") != ApplicationId)
        {
            throw new InvalidDataException("The file is not a supported OpenBreak database.");
        }

        var version = ReadPragma(connection, null, "PRAGMA user_version");
        if (version is >= 1 and < SchemaVersion)
        {
            using var transaction = connection.BeginTransaction();
            version = ReadPragma(connection, transaction, "PRAGMA user_version");
            if (version is >= 1 and < SchemaVersion)
            {
                if (ReadPragma(connection, transaction, "PRAGMA application_id") != ApplicationId)
                {
                    throw new InvalidDataException("The file is not a supported OpenBreak database.");
                }
                using var integrity = Command(connection, transaction, "PRAGMA quick_check(1)");
                if (integrity.ExecuteScalar() as string != "ok")
                {
                    throw new InvalidDataException("The task database did not pass its integrity check.");
                }
                using var tasks = Command(connection, transaction,
                    "SELECT id, title, next_step, status, position, archived FROM tasks LIMIT 0");
                using (tasks.ExecuteReader()) { }
                if (version >= 3)
                {
                    using var capture = Command(connection, transaction,
                        "SELECT id, shortcut, guide_dismissed FROM capture_preferences LIMIT 0");
                    using (capture.ExecuteReader()) { }
                }
                if (version >= 4)
                {
                    MigrateAppearance(connection, transaction, version);
                }
                if (version >= 2)
                {
                    MigrateReminderSettings(connection, transaction, version);
                }
                if (version >= 9)
                {
                    ReadFloatingTimerPosition(connection, transaction);
                }
                using var migration = Command(connection, transaction, $"""
                    {(version == 1 ? ReminderTable : "")}
                    {(version <= 2 ? CaptureTable : "")}
                    {(version <= 3 ? AppearanceTable : "")}
                    {(version < 9 ? FloatingTimerPlacementTable : "")}
                    PRAGMA user_version = {SchemaVersion};
                    """);
                migration.ExecuteNonQuery();
                version = SchemaVersion;
            }
            transaction.Commit();
        }

        if (version != SchemaVersion)
        {
            throw new InvalidDataException("The file is not a supported OpenBreak database.");
        }
    }

    private static long ReadPragma(SqliteConnection connection, SqliteTransaction? transaction, string text)
    {
        using var command = Command(connection, transaction, text);
        return (long)command.ExecuteScalar()!;
    }

    private static TaskStatus GetStatus(SqliteConnection connection, SqliteTransaction transaction, long id, bool archived)
    {
        using var query = Command(connection, transaction, "SELECT status FROM tasks WHERE id = $id AND archived = $archived",
            ("$id", id), ("$archived", archived));
        return query.ExecuteScalar() is long status
            ? (TaskStatus)status
            : throw new InvalidDataException("The task is no longer available.");
    }

    private static long NextPosition(SqliteConnection connection, SqliteTransaction transaction, TaskStatus status, bool archived)
    {
        using var query = Command(connection, transaction, """
            SELECT COALESCE(MAX(position), -1) + 1 FROM tasks WHERE status = $status AND archived = $archived
            """, ("$status", (int)status), ("$archived", archived));
        return (long)query.ExecuteScalar()!;
    }

    private static List<long> ActiveIds(SqliteConnection connection, SqliteTransaction transaction, TaskStatus status)
    {
        using var query = Command(connection, transaction, """
            SELECT id FROM tasks WHERE status = $status AND archived = 0 ORDER BY position, id
            """, ("$status", (int)status));
        using var reader = query.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static void SaveOrder(SqliteConnection connection, SqliteTransaction transaction, TaskStatus status, List<long> ids)
    {
        using var update = Command(connection, transaction, """
            UPDATE tasks SET status = $status, position = $position WHERE id = $id AND archived = 0
            """, ("$status", (int)status), ("$position", 0), ("$id", 0L));
        for (var position = 0; position < ids.Count; position++)
        {
            update.Parameters["$position"].Value = position;
            update.Parameters["$id"].Value = ids[position];
            EnsureTaskChanged(update);
        }
    }

    private static void EnsureTaskChanged(SqliteCommand command)
    {
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("The task is no longer available.");
        }
    }

    private static string NormalizeTitle(string title)
    {
        var text = title.Trim();
        return text.Length is > 0 and <= TitleLimit
            ? text
            : throw new ArgumentException("The task title is missing or too long.", nameof(title));
    }

    private static string? NormalizeNextStep(string? nextStep)
    {
        var text = nextStep?.Trim();
        if (text?.Length > NextStepLimit)
        {
            throw new ArgumentException("The next step is too long.", nameof(nextStep));
        }
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static BoardTask ReadTask(SqliteDataReader reader)
    {
        var status = reader.GetInt64(3);
        if (status is < (long)TaskStatus.ToDo or > (long)TaskStatus.Done)
        {
            throw new InvalidDataException("The saved task status is invalid.");
        }
        return new BoardTask(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), (TaskStatus)status);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string text,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }
}
