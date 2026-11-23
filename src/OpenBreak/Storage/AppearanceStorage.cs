using Microsoft.Data.Sqlite;

namespace OpenBreak;

internal sealed partial class TaskStore
{
    private const string SceneMotionColumn = "scene_motion TEXT NOT NULL DEFAULT 'FollowWindows' CHECK (scene_motion IN ('FollowWindows', 'Off'))";
    private const string BreakSoundColumn = "break_start_sound INTEGER NOT NULL DEFAULT 0 CHECK (break_start_sound IN (0, 1))";
    private const string TrayCountdownColumn = "tray_countdown INTEGER NOT NULL DEFAULT 0 CHECK (tray_countdown IN (0, 1))";
    private const string FloatingTimerColumn = "floating_timer INTEGER NOT NULL DEFAULT 0 CHECK (floating_timer IN (0, 1))";
    private const string FloatingOnTopColumn = "floating_on_top INTEGER NOT NULL DEFAULT 0 CHECK (floating_on_top IN (0, 1))";
    private const string FloatingTimerStyleColumn = "floating_timer_style TEXT NOT NULL DEFAULT 'Standard' CHECK (floating_timer_style IN ('Standard', 'Compact'))";
    private const string AppearanceTable = $"""
        CREATE TABLE appearance_preferences (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            theme TEXT NOT NULL CHECK (theme IN ('System', 'Light', 'Dark')),
            scene TEXT NOT NULL CHECK (scene IN ('Dusk', 'Daylight')),
            {SceneMotionColumn},
            {BreakSoundColumn},
            {TrayCountdownColumn},
            {FloatingTimerColumn},
            {FloatingOnTopColumn},
            {FloatingTimerStyleColumn}
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
            SELECT id, theme, scene, scene_motion, break_start_sound, tray_countdown, floating_timer, floating_on_top,
                floating_timer_style
            FROM appearance_preferences
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
            !TryReadFlag(reader.GetValue(4), out var sound) ||
            !TryReadFlag(reader.GetValue(5), out var trayCountdown) ||
            !TryReadFlag(reader.GetValue(6), out var floatingTimer) ||
            !TryReadFlag(reader.GetValue(7), out var floatingOnTop) ||
            reader.GetValue(8) is not string styleName || !TryReadEnum(styleName, out FloatingTimerStyle style) ||
            reader.Read())
        {
            throw new InvalidDataException("The saved appearance preferences are invalid.");
        }

        return new AppearancePreferences(theme, scene, motion, sound, trayCountdown, floatingTimer, floatingOnTop, style);
    }

    private static bool TryReadFlag(object value, out bool enabled)
    {
        enabled = value is 1L;
        return value is 0L or 1L;
    }

    // Enum.TryParse also accepts numeric values and whitespace. Only accept the stored names.
    private static bool TryReadEnum<T>(string name, out T value) where T : struct, Enum =>
        Enum.TryParse(name, out value) && Enum.IsDefined(value) && value.ToString() == name;

    private static void MigrateAppearance(SqliteConnection connection, SqliteTransaction transaction, long version)
    {
        if (version < 8)
        {
            AddAppearanceColumn(SceneMotionColumn);
            AddAppearanceColumn(BreakSoundColumn);
        }
        if (version < 9)
        {
            AddAppearanceColumn(TrayCountdownColumn);
            AddAppearanceColumn(FloatingTimerColumn);
            AddAppearanceColumn(FloatingOnTopColumn);
        }
        AddAppearanceColumn(FloatingTimerStyleColumn);
        ReadAppearance(connection, transaction);

        void AddAppearanceColumn(string column)
        {
            using var add = Command(connection, transaction, $"ALTER TABLE appearance_preferences ADD COLUMN {column}");
            add.ExecuteNonQuery();
        }
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
            INSERT INTO appearance_preferences
                (id, theme, scene, scene_motion, break_start_sound, tray_countdown, floating_timer, floating_on_top,
                 floating_timer_style)
            VALUES (1, $theme, $scene, $motion, $sound, $tray, $floating, $top, $style)
            ON CONFLICT (id) DO UPDATE SET theme = excluded.theme, scene = excluded.scene,
                scene_motion = excluded.scene_motion, break_start_sound = excluded.break_start_sound,
                tray_countdown = excluded.tray_countdown, floating_timer = excluded.floating_timer,
                floating_on_top = excluded.floating_on_top, floating_timer_style = excluded.floating_timer_style
            """, ("$theme", preferences.Theme.ToString()), ("$scene", preferences.Scene.ToString()),
            ("$motion", preferences.Motion.ToString()), ("$sound", preferences.BreakStartSound ? 1 : 0),
            ("$tray", preferences.TrayCountdown ? 1 : 0), ("$floating", preferences.FloatingTimer ? 1 : 0),
            ("$top", preferences.FloatingTimerOnTop ? 1 : 0), ("$style", preferences.TimerStyle.ToString()));
        if (command.ExecuteNonQuery() != 1 || ReadAppearance(connection, transaction) != preferences)
        {
            throw new InvalidDataException("The appearance preferences could not be saved.");
        }
        transaction.Commit();
    }
}
