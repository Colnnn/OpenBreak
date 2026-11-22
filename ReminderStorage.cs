using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Afterbreak;

internal sealed partial class TaskStore
{
    private sealed record LegacyReminderSettings(bool Enabled, int WorkSeconds, int ShortSeconds, int LongSeconds,
        bool WorkingHours, int StartMinute, int EndMinute, bool TimerOnly)
    {
        internal ReminderSettings Upgrade() => new(Enabled, WorkSeconds, ShortSeconds, LongSeconds, WorkingHours,
            StartMinute, EndMinute, TimerOnly ? BreakDisplay.TimerOnly : BreakDisplay.SavedTaskBoard);
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
        SqliteConnection connection, SqliteTransaction? transaction, bool legacy = false)
    {
        using var query = Command(connection, transaction,
            $"SELECT id, settings, checkpoint, {(legacy ? "0" : "work_paused")} FROM reminder_state");
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
        var settings = ReadReminderJson<ReminderSettings>(settingsJson);
        var checkpoint = ReadReminderJson<ReminderCheckpoint>(checkpointJson) with { WorkPaused = workPaused == 1 };
        ValidateReminderState(settings, checkpoint);
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

    private static void ValidateReminderState(ReminderSettings settings, ReminderCheckpoint checkpoint)
    {
        if (!settings.IsValid || !Enum.IsDefined(checkpoint.Phase) ||
            checkpoint.CompletedShorts is < 0 or > 3 || checkpoint.Remaining < TimeSpan.Zero ||
            checkpoint.Remaining > TimeSpan.FromDays(1) || checkpoint.Away < TimeSpan.Zero ||
            checkpoint.Away > TimeSpan.FromHours(1) || checkpoint.SavedAt == default ||
            (checkpoint.WorkPaused && (!settings.Enabled || checkpoint.Remaining <= TimeSpan.Zero ||
                checkpoint.Phase is not (ReminderPhase.Work or ReminderPhase.Snoozed) ||
                (checkpoint.Phase == ReminderPhase.Snoozed && checkpoint.Remaining > ReminderScheduler.SnoozeLength))))
        {
            throw new InvalidDataException("The saved reminder settings or checkpoint are invalid.");
        }
    }

    private static void MigrateWorkPause(SqliteConnection connection, SqliteTransaction transaction)
    {
        ReadReminders(connection, transaction, legacy: true);
        using var add = Command(connection, transaction, $"ALTER TABLE reminder_state ADD COLUMN {WorkPauseColumn}");
        add.ExecuteNonQuery();
    }

    private static void MigrateReminderDisplay(SqliteConnection connection, SqliteTransaction transaction)
    {
        ReminderSettings settings;
        using (var query = Command(connection, transaction, "SELECT id, settings, checkpoint FROM reminder_state"))
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read())
            {
                return;
            }
            if (reader.GetValue(0) is not 1L || reader.GetValue(1) is not string settingsJson ||
                reader.GetValue(2) is not string checkpointJson)
            {
                throw new InvalidDataException("The saved reminder row is invalid.");
            }
            settings = ReadReminderJson<LegacyReminderSettings>(settingsJson).Upgrade();
            ValidateReminderState(settings, ReadReminderJson<ReminderCheckpoint>(checkpointJson));
            if (reader.Read())
            {
                throw new InvalidDataException("The saved reminder row is invalid.");
            }
        }

        using var update = Command(connection, transaction, "UPDATE reminder_state SET settings = $settings WHERE id = 1",
            ("$settings", JsonSerializer.Serialize(settings, ReminderJson)));
        if (update.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("The saved break display could not be migrated.");
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
