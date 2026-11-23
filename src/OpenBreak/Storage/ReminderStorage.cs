using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBreak;

internal sealed partial class TaskStore
{
    private sealed record LegacyDisplaySettings(bool Enabled, int WorkSeconds, int ShortSeconds, int LongSeconds,
        bool WorkingHours, int StartMinute, int EndMinute, bool TimerOnly)
    {
        internal ReminderSettings Upgrade() => new(Enabled, WorkSeconds, ShortSeconds, LongSeconds, WorkingHours,
            StartMinute, EndMinute, TimerOnly ? BreakDisplay.TimerOnly : BreakDisplay.SavedTaskBoard);
    }

    private sealed record LegacyRhythmSettings(bool Enabled, int WorkSeconds, int ShortSeconds, int LongSeconds,
        bool WorkingHours, int StartMinute, int EndMinute, BreakDisplay BreakDisplay)
    {
        internal ReminderSettings Upgrade() => new(Enabled, WorkSeconds, ShortSeconds, LongSeconds, WorkingHours,
            StartMinute, EndMinute, BreakDisplay);
    }

    private static readonly JsonSerializerOptions ReminderJson = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    private const string WorkPauseColumn = "work_paused INTEGER NOT NULL DEFAULT 0 CHECK (work_paused IN (0, 1))";

    private const string ReminderTable = $"""
        CREATE TABLE reminder_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            settings TEXT NOT NULL,
            checkpoint TEXT NOT NULL,
            {WorkPauseColumn}
        ) STRICT;
        """;

    internal (ReminderSettings Settings, ReminderCheckpoint? Checkpoint) LoadReminders()
    {
        using var connection = Open();
        return ReadReminders(connection, null);
    }

    private static (ReminderSettings Settings, ReminderCheckpoint? Checkpoint) ReadReminders(
        SqliteConnection connection, SqliteTransaction? transaction, long version = SchemaVersion)
    {
        using var query = Command(connection, transaction,
            $"SELECT id, settings, checkpoint, {(version < 6 ? "0" : "work_paused")} FROM reminder_state");
        using var reader = query.ExecuteReader();
        if (!reader.Read())
        {
            return (ReminderSettings.Default, null);
        }

        if (reader.GetValue(0) is not 1L || reader.GetValue(1) is not string settingsJson ||
            reader.GetValue(2) is not string checkpointJson || reader.GetValue(3) is not long workPaused ||
            workPaused is not (0 or 1))
        {
            throw new InvalidDataException("The saved reminder row is invalid.");
        }
        var settings = version switch
        {
            <= 4 => ReadReminderJson<LegacyDisplaySettings>(settingsJson).Upgrade(),
            <= 6 => ReadReminderJson<LegacyRhythmSettings>(settingsJson).Upgrade(),
            _ => ReadReminderJson<ReminderSettings>(settingsJson),
        };
        var checkpoint = ReadReminderJson<ReminderCheckpoint>(checkpointJson) with { WorkPaused = workPaused == 1 };
        ValidateReminderState(settings, checkpoint, version <= 6);
        if (reader.Read())
        {
            throw new InvalidDataException("The saved reminder row is invalid.");
        }
        return (settings, checkpoint);
    }

    internal void SaveBreakDisplay(BreakDisplay display, ReminderSettings current, ReminderCheckpoint initialCheckpoint)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var (saved, checkpoint) = ReadReminders(connection, transaction);
        var settings = saved with { BreakDisplay = display };
        var activeSettings = current with { BreakDisplay = display };
        var activeCheckpoint = checkpoint ?? initialCheckpoint;
        ValidateReminderState(settings, activeCheckpoint);
        if (settings != activeSettings || activeCheckpoint.ManuallyPaused)
        {
            throw new InvalidDataException("The saved reminder state is unavailable for a preview.");
        }

        var settingsJson = JsonSerializer.Serialize(settings, ReminderJson);
        using var save = checkpoint is null
            ? Command(connection, transaction,
                "INSERT INTO reminder_state (id, settings, checkpoint, work_paused) VALUES (1, $settings, $checkpoint, $paused)",
                ("$settings", settingsJson), ("$checkpoint", JsonSerializer.Serialize(initialCheckpoint, ReminderJson)),
                ("$paused", initialCheckpoint.WorkPaused))
            : Command(connection, transaction, "UPDATE reminder_state SET settings = $settings WHERE id = 1",
                ("$settings", settingsJson));
        if (save.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("The break display could not be saved.");
        }
        transaction.Commit();
    }

    private static T ReadReminderJson<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ReminderJson) ??
                throw new InvalidDataException("The saved reminder settings or checkpoint are invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved reminder settings or checkpoint are invalid.", exception);
        }
    }

    private static void ValidateReminderState(ReminderSettings settings, ReminderCheckpoint checkpoint, bool legacy = false)
    {
        var maxSnoozeSeconds = legacy ? ReminderSettings.DefaultSnoozeSeconds : ReminderSettings.MaxSnoozeSeconds;
        var maxCompletedShorts = settings.LongBreaks ? settings.ShortsBeforeLong : 0;
        if (!settings.IsValid || !Enum.IsDefined(checkpoint.Phase) ||
            checkpoint.CompletedShorts < 0 || checkpoint.CompletedShorts > maxCompletedShorts || checkpoint.Remaining < TimeSpan.Zero ||
            checkpoint.Remaining > TimeSpan.FromDays(1) || checkpoint.Away < TimeSpan.Zero ||
            checkpoint.Away > TimeSpan.FromHours(1) || checkpoint.SavedAt == default ||
            (checkpoint.Phase == ReminderPhase.Snoozed && checkpoint.Remaining > TimeSpan.FromSeconds(maxSnoozeSeconds)) ||
            (checkpoint.WorkPaused && (!settings.Enabled || checkpoint.Remaining <= TimeSpan.Zero ||
                checkpoint.Phase is not (ReminderPhase.Work or ReminderPhase.Snoozed))))
        {
            throw new InvalidDataException("The saved reminder settings or checkpoint are invalid.");
        }
    }

    private static void MigrateReminderSettings(SqliteConnection connection, SqliteTransaction transaction, long version)
    {
        var (settings, checkpoint) = ReadReminders(connection, transaction, version);
        if (version < 6)
        {
            using var add = Command(connection, transaction, $"ALTER TABLE reminder_state ADD COLUMN {WorkPauseColumn}");
            add.ExecuteNonQuery();
        }
        if (checkpoint is null)
        {
            return;
        }

        using var update = Command(connection, transaction, "UPDATE reminder_state SET settings = $settings WHERE id = 1",
            ("$settings", JsonSerializer.Serialize(settings, ReminderJson)));
        if (update.ExecuteNonQuery() != 1 || ReadReminders(connection, transaction) != (settings, checkpoint))
        {
            throw new InvalidDataException("The saved reminder settings could not be migrated.");
        }
    }

    internal void SaveReminders(ReminderSettings settings, ReminderCheckpoint checkpoint)
    {
        ValidateReminderState(settings, checkpoint);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var save = Command(connection, transaction, """
            INSERT INTO reminder_state (id, settings, checkpoint, work_paused) VALUES (1, $settings, $checkpoint, $paused)
            ON CONFLICT (id) DO UPDATE SET settings = excluded.settings, checkpoint = excluded.checkpoint, work_paused = excluded.work_paused
            """, ("$settings", JsonSerializer.Serialize(settings, ReminderJson)),
            ("$checkpoint", JsonSerializer.Serialize(checkpoint, ReminderJson)), ("$paused", checkpoint.WorkPaused));
        if (save.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("The reminder settings could not be saved.");
        }
        transaction.Commit();
    }
}
