using Microsoft.Data.Sqlite;

namespace Afterbreak;

internal readonly record struct FloatingTimerPosition(string Monitor, int Horizontal, int Vertical)
{
    internal const int Extent = 10000;
    internal bool IsValid => Monitor is { Length: > 0 and <= 31 } && !Monitor.Any(char.IsControl) &&
        Horizontal is >= 0 and <= Extent && Vertical is >= 0 and <= Extent;
}

internal sealed partial class TaskStore
{
    private static readonly string FloatingTimerPlacementTable = $"""
        CREATE TABLE floating_timer_position (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            monitor TEXT NOT NULL CHECK (length(monitor) BETWEEN 1 AND 31),
            horizontal INTEGER NOT NULL CHECK (horizontal BETWEEN 0 AND {FloatingTimerPosition.Extent}),
            vertical INTEGER NOT NULL CHECK (vertical BETWEEN 0 AND {FloatingTimerPosition.Extent})
        ) STRICT;
        """;

    internal FloatingTimerPosition? LoadFloatingTimerPosition()
    {
        using var connection = Open();
        return ReadFloatingTimerPosition(connection, null);
    }

    internal void SaveFloatingTimerPosition(FloatingTimerPosition position)
    {
        if (!position.IsValid)
        {
            throw new ArgumentException("Invalid floating timer position.", nameof(position));
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection, transaction, """
            INSERT INTO floating_timer_position (id, monitor, horizontal, vertical) VALUES (1, $monitor, $x, $y)
            ON CONFLICT (id) DO UPDATE SET monitor = excluded.monitor, horizontal = excluded.horizontal,
                vertical = excluded.vertical
            """, ("$monitor", position.Monitor), ("$x", position.Horizontal), ("$y", position.Vertical));
        if (command.ExecuteNonQuery() != 1 || ReadFloatingTimerPosition(connection, transaction) != position)
        {
            throw new InvalidDataException("The floating timer position could not be saved.");
        }
        transaction.Commit();
    }

    private static FloatingTimerPosition? ReadFloatingTimerPosition(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT id, monitor, horizontal, vertical FROM floating_timer_position");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        if (reader.GetValue(0) is not 1L || reader.GetValue(1) is not string monitor ||
            reader.GetValue(2) is not long x || x is < 0 or > FloatingTimerPosition.Extent ||
            reader.GetValue(3) is not long y || y is < 0 or > FloatingTimerPosition.Extent ||
            reader.Read())
        {
            throw new InvalidDataException("The saved floating timer position is invalid.");
        }
        var position = new FloatingTimerPosition(monitor, (int)x, (int)y);
        return position.IsValid ? position : throw new InvalidDataException("The saved floating timer monitor is invalid.");
    }
}
