namespace Afterbreak;

internal sealed partial class TaskStore
{
    private const string AppearanceTable = """
        CREATE TABLE appearance_preferences (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            theme TEXT NOT NULL CHECK (theme IN ('System', 'Light', 'Dark')),
            scene TEXT NOT NULL CHECK (scene IN ('Dusk', 'Daylight'))
        ) STRICT;
        """;

    internal AppearancePreferences LoadAppearance()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT theme, scene FROM appearance_preferences WHERE id = 1");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return AppearancePreferences.Default;
        }
        if (!Enum.TryParse(reader.GetString(0), out AppearanceTheme theme) || !Enum.TryParse(reader.GetString(1), out BreakScene scene))
        {
            throw new InvalidDataException("The saved appearance preferences are invalid.");
        }

        var preferences = new AppearancePreferences(theme, scene);
        return preferences.IsValid ? preferences : throw new InvalidDataException("The saved appearance preferences are invalid.");
    }

    internal void SaveAppearance(AppearancePreferences preferences)
    {
        if (!preferences.IsValid)
        {
            throw new ArgumentException("Invalid appearance preferences.", nameof(preferences));
        }

        using var connection = Open();
        using var command = Command(connection, null, """
            INSERT INTO appearance_preferences (id, theme, scene) VALUES (1, $theme, $scene)
            ON CONFLICT (id) DO UPDATE SET theme = excluded.theme, scene = excluded.scene
            """, ("$theme", preferences.Theme.ToString()), ("$scene", preferences.Scene.ToString()));
        command.ExecuteNonQuery();
    }
}
