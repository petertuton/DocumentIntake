namespace DocumentIntake.Functions.Orchestrations;

/// <summary>
/// Deterministic, replay-safe polling schedule: poll quickly at first, then back off,
/// so short documents finish fast without hammering the service on long ones.
/// </summary>
public static class PollingSchedule
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>Longest interval used once the ramp is exhausted.</summary>
    public static TimeSpan MaxDelay => Delays[^1];

    /// <summary>Delay to wait before poll number <paramref name="attempt"/> (0-based).</summary>
    public static TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        return attempt < Delays.Length ? Delays[attempt] : MaxDelay;
    }
}
