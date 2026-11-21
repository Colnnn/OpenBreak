using System.Text.Json;

namespace Afterbreak;

internal sealed partial class TaskStore
{
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

        try
        {
            var settings = JsonSerializer.Deserialize<ReminderSettings>(reader.GetString(0));
            var checkpoint = JsonSerializer.Deserialize<ReminderCheckpoint>(reader.GetString(1));
            if (settings is not { IsValid: true } || checkpoint is null || !Enum.IsDefined(checkpoint.Phase) ||
                checkpoint.CompletedShorts is < 0 or > 3 || checkpoint.Remaining < TimeSpan.Zero ||
                checkpoint.Remaining > TimeSpan.FromDays(1) || checkpoint.Away < TimeSpan.Zero ||
                checkpoint.Away > TimeSpan.FromHours(1) || checkpoint.SavedAt == default)
            {
                throw new InvalidDataException("The saved reminder settings or checkpoint are invalid.");
            }

            return (settings, checkpoint);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved reminder settings or checkpoint are invalid.", exception);
        }
    }

    internal void SaveReminders(ReminderSettings settings, ReminderCheckpoint checkpoint)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var save = Command(connection, transaction, """
            INSERT INTO reminder_state (id, settings, checkpoint) VALUES (1, $settings, $checkpoint)
            ON CONFLICT (id) DO UPDATE SET settings = excluded.settings, checkpoint = excluded.checkpoint
            """, ("$settings", JsonSerializer.Serialize(settings)), ("$checkpoint", JsonSerializer.Serialize(checkpoint)));
        save.ExecuteNonQuery();
        transaction.Commit();
    }
}
