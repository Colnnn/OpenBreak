using Microsoft.Data.Sqlite;

namespace Afterbreak;

internal sealed partial class TaskStore
{
    private const string SceneMotionColumn = "scene_motion TEXT NOT NULL DEFAULT 'FollowWindows' CHECK (scene_motion IN ('FollowWindows', 'Off'))";
    private const string BreakSoundColumn = "break_start_sound INTEGER NOT NULL DEFAULT 0 CHECK (break_start_sound IN (0, 1))";
    private const string AppearanceTable = $"""
        CREATE TABLE appearance_preferences (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            theme TEXT NOT NULL CHECK (theme IN ('System', 'Light', 'Dark')),
            scene TEXT NOT NULL CHECK (scene IN ('Dusk', 'Daylight')),
            {SceneMotionColumn},
            {BreakSoundColumn}
        ) STRICT;
        """;

    internal AppearancePreferences LoadAppearance()
    {
        using var connection = Open();
        return ReadAppearance(connection, null) ?? AppearancePreferences.Default;
    }

    private static AppearancePreferences? ReadAppearance(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, """
            SELECT id, theme, scene, scene_motion, break_start_sound FROM appearance_preferences
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        if (reader.GetValue(0) is not 1L ||
            reader.GetValue(1) is not string themeName || !TryReadEnum(themeName, out AppearanceTheme theme) ||
            reader.GetValue(2) is not string sceneName || !TryReadEnum(sceneName, out BreakScene scene) ||
            reader.GetValue(3) is not string motionName || !TryReadEnum(motionName, out SceneMotion motion) ||
            reader.GetValue(4) is not long sound || sound is not (0 or 1) || reader.Read())
        {
            throw new InvalidDataException("The saved appearance preferences are invalid.");
        }

        return new AppearancePreferences(theme, scene, motion, sound == 1);
    }

    // Enum.TryParse also accepts numeric values and whitespace. Only accept the stored names.
    private static bool TryReadEnum<T>(string name, out T value) where T : struct, Enum =>
        Enum.TryParse(name, out value) && Enum.IsDefined(value) && value.ToString() == name;

    private static void MigrateAppearance(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var addMotion = Command(connection, transaction, $"ALTER TABLE appearance_preferences ADD COLUMN {SceneMotionColumn}");
        addMotion.ExecuteNonQuery();
        using var addSound = Command(connection, transaction, $"ALTER TABLE appearance_preferences ADD COLUMN {BreakSoundColumn}");
        addSound.ExecuteNonQuery();
        ReadAppearance(connection, transaction);
    }

    internal void SaveAppearance(AppearancePreferences preferences)
    {
        if (!preferences.IsValid)
        {
            throw new ArgumentException("Invalid appearance preferences.", nameof(preferences));
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection, transaction, """
            INSERT INTO appearance_preferences (id, theme, scene, scene_motion, break_start_sound)
            VALUES (1, $theme, $scene, $motion, $sound)
            ON CONFLICT (id) DO UPDATE SET theme = excluded.theme, scene = excluded.scene,
                scene_motion = excluded.scene_motion, break_start_sound = excluded.break_start_sound
            """, ("$theme", preferences.Theme.ToString()), ("$scene", preferences.Scene.ToString()),
            ("$motion", preferences.Motion.ToString()), ("$sound", preferences.BreakStartSound ? 1 : 0));
        if (command.ExecuteNonQuery() != 1 || ReadAppearance(connection, transaction) != preferences)
        {
            throw new InvalidDataException("The appearance preferences could not be saved.");
        }
        transaction.Commit();
    }
}
