namespace Afterbreak;

internal sealed partial class TaskStore
{
    private const string CaptureTable = """
        CREATE TABLE capture_preferences (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            shortcut TEXT NOT NULL,
            guide_dismissed INTEGER NOT NULL CHECK (guide_dismissed IN (0, 1))
        ) STRICT;
        """;

    internal CapturePreferences LoadCapturePreferences()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT shortcut, guide_dismissed FROM capture_preferences WHERE id = 1");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new CapturePreferences(CaptureShortcut.Default, false);
        }
        if (!CaptureShortcut.TryParse(reader.GetString(0), out var shortcut))
        {
            throw new InvalidDataException("The saved capture shortcut is invalid.");
        }
        return new CapturePreferences(shortcut, reader.GetBoolean(1));
    }

    internal void SaveCaptureShortcut(CaptureShortcut shortcut)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            INSERT INTO capture_preferences (id, shortcut, guide_dismissed) VALUES (1, $shortcut, 0)
            ON CONFLICT (id) DO UPDATE SET shortcut = excluded.shortcut
            """, ("$shortcut", shortcut.ToString()));
        command.ExecuteNonQuery();
    }

    internal void DismissCaptureGuide()
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            INSERT INTO capture_preferences (id, shortcut, guide_dismissed) VALUES (1, $default, 1)
            ON CONFLICT (id) DO UPDATE SET guide_dismissed = 1
            """, ("$default", CaptureShortcut.Default.ToString()));
        command.ExecuteNonQuery();
    }
}
