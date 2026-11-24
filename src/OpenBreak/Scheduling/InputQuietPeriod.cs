namespace OpenBreak;

internal readonly record struct LastInputSample(uint NowTick, uint LastInputTick);

internal struct InputQuietPeriod
{
    internal static readonly TimeSpan FinalSecond = TimeSpan.FromSeconds(1);
    private const uint QuietMilliseconds = 3000;
    private static readonly TimeSpan QuietLength = TimeSpan.FromMilliseconds(QuietMilliseconds);
    private uint? lastInputTick;
    private long observedAt;
    private TimeSpan quietDelay;
    private long? unavailableSince;

    internal static bool IsFinalSecond(TimeSpan remaining, TimeSpan elapsed) => remaining - elapsed <= FinalSecond;

    internal bool ShouldHold(TimeProvider clock, long now, LastInputSample? sample)
    {
        if (sample is not { } input)
        {
            // Allow one bounded retry window per warning, even if failures are intermittent.
            unavailableSince ??= now;
            if (clock.GetElapsedTime(unavailableSince.Value, now) < QuietLength)
            {
                return true;
            }
        }
        else if (input.LastInputTick != lastInputTick)
        {
            var wasSampled = lastInputTick.HasValue;
            lastInputTick = input.LastInputTick;
            observedAt = now;
            if (wasSampled)
            {
                // Windows can report a lower tick for a new event. Use observation time for changes.
                quietDelay = QuietLength;
            }
            else
            {
                // Subtraction works across uint wrap. Future or ambiguous ages fall outside this window.
                var age = unchecked(input.NowTick - input.LastInputTick);
                quietDelay = age < QuietMilliseconds ? TimeSpan.FromMilliseconds(QuietMilliseconds - age) : TimeSpan.Zero;
            }
        }

        return quietDelay > TimeSpan.Zero && clock.GetElapsedTime(observedAt, now) < quietDelay;
    }
}
