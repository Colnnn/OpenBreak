namespace Afterbreak;

internal sealed class TestClock(DateTimeOffset utc) : TimeProvider
{
    private long timestamp;
    private DateTimeOffset wall = utc;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public override DateTimeOffset GetUtcNow() => wall;

    internal void Advance(TimeSpan duration)
    {
        timestamp += duration.Ticks;
        ShiftWall(duration);
    }

    internal void ShiftWall(TimeSpan difference) => wall += difference;
}
