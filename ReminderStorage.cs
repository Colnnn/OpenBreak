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

    private const string ReminderTable = """
        CREATE TABLE reminder_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            settings TEXT NOT NULL,
            checkpoint TEXT NOT NULL
        ) STRICT;
        """;

    internal (ReminderSettings Settings, ReminderCheckpoint? Checkpoint) LoadReminders()
    {
        using var connection = Open();
        using var query = Command(connection, null, "SELECT settings, checkpoint FROM reminder_state WHERE id = 1");
        using var reader = query.ExecuteReader();
        if (!reader.Read())
        {
            return (ReminderSettings.Default, null);
        }

        var settings = ReadReminderJson<ReminderSettings>(reader.GetString(0));
        var checkpoint = ReadReminderJson<ReminderCheckpoint>(reader.GetString(1));
        ValidateReminderState(settings, checkpoint);
        return (settings, checkpoint);
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
            checkpoint.Away > TimeSpan.FromHours(1) || checkpoint.SavedAt == default)
        {
            throw new InvalidDataException("The saved reminder settings or checkpoint are invalid.");
        }
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
            INSERT INTO reminder_state (id, settings, checkpoint) VALUES (1, $settings, $checkpoint)
            ON CONFLICT (id) DO UPDATE SET settings = excluded.settings, checkpoint = excluded.checkpoint
            """, ("$settings", JsonSerializer.Serialize(settings, ReminderJson)), ("$checkpoint", JsonSerializer.Serialize(checkpoint, ReminderJson)));
        if (save.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("The reminder settings could not be saved.");
        }
        transaction.Commit();
    }
}
