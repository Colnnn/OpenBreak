namespace OpenBreak;

internal static class InputQuietPeriodChecks
{
    internal static void Run()
    {
        var clock = new MillisecondClock();
        var quiet = new InputQuietPeriod();

        Check(!InputQuietPeriod.IsFinalSecond(TimeSpan.FromMilliseconds(1100), TimeSpan.Zero), "Sampling starts at the final second");
        Check(InputQuietPeriod.IsFinalSecond(TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(1200)), "A delayed tick crosses the gate");
        Check(InputQuietPeriod.IsFinalSecond(TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(100)), "The gate includes exactly one second");

        Check(quiet.ShouldHold(clock, 0, null), "An initial query failure starts a bounded retry");
        Check(quiet.ShouldHold(clock, 100, new LastInputSample(100_000, 99_000)), "The first valid recent sample holds");
        Check(quiet.ShouldHold(clock, 2099, new LastInputSample(101_999, 99_000)), "The hold uses the remaining part of the quiet period");
        Check(!quiet.ShouldHold(clock, 2100, new LastInputSample(102_000, 99_000)), "Three seconds of quiet release the hold");
        Check(quiet.ShouldHold(clock, 2200, new LastInputSample(102_100, 90_000)), "A changed timestamp can regress");
        Check(quiet.ShouldHold(clock, 5199, null), "A failed query preserves the known hold");
        Check(!quiet.ShouldHold(clock, 5200, null), "Later failures cannot restart the per-warning retry or extend known activity");

        quiet = default;
        Check(quiet.ShouldHold(clock, 0, null) && quiet.ShouldHold(clock, 2999, null) &&
            !quiet.ShouldHold(clock, 3000, null), "Repeated query failure falls back after three seconds");
        Check(!quiet.ShouldHold(clock, 3001, new LastInputSample(100_000, 90_000)) &&
            !quiet.ShouldHold(clock, 3002, null), "Intermittent successful queries cannot restart the retry");

        quiet = default;
        Check(quiet.ShouldHold(clock, 0, new LastInputSample(400, uint.MaxValue - 599)), "An initial recent timestamp can precede a wrap");
        Check(!quiet.ShouldHold(clock, 2000, new LastInputSample(2400, uint.MaxValue - 599)), "The wrapped sample releases on time");
        Check(quiet.ShouldHold(clock, 2001, new LastInputSample(2401, 2400)), "New input can follow a wrap");

        quiet = default;
        Check(!quiet.ShouldHold(clock, 50, new LastInputSample(200, 201)), "An initial future tick is implausible");
        Check(!quiet.ShouldHold(clock, 51, new LastInputSample(201, 201)), "The same invalid tick cannot establish a hold later");
        Check(quiet.ShouldHold(clock, 52, new LastInputSample(202, 204)), "A changed anomalous tick holds from observation");
        Check(!quiet.ShouldHold(clock, 3052, new LastInputSample(3202, 204)), "An unchanged anomalous tick cannot prolong the hold");

        var candidate = quiet;
        Check(candidate.ShouldHold(clock, 3053, new LastInputSample(3203, 3203)), "A candidate can track new activity");
        Check(!quiet.ShouldHold(clock, 3053, new LastInputSample(3203, 204)), "A copied candidate does not mutate the original");
        candidate = default;
        Check(candidate.ShouldHold(clock, 3053, null), "Reset restores the bounded retry for a new warning");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }
        Console.WriteLine($"PASS {description}");
    }

    private sealed class MillisecondClock : TimeProvider
    {
        public override long TimestampFrequency => 1000;
    }
}
