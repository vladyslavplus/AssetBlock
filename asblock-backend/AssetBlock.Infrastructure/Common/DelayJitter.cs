using System.Security.Cryptography;

namespace AssetBlock.Infrastructure.Common;

/// <summary>
/// Applies approximately ±20% jitter to backoff and polling durations to prevent thundering herds.
/// Produces delays in the range [0.8 * baseDelay, 1.2 * baseDelay].
/// </summary>
internal static class DelayJitter
{
    private const double DEFAULT_JITTER_RATIO = 0.20;

    /// <summary>
    /// Applies ±20% jitter to the given base duration.
    /// An optional randomProvider can be passed for deterministic testing (returning [0.0, 1.0]).
    /// </summary>
    public static TimeSpan Apply(TimeSpan baseDelay, Func<double>? randomProvider = null)
    {
        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var randomSample = randomProvider is not null
            ? Math.Clamp(randomProvider(), 0.0, 1.0)
            : RandomNumberGenerator.GetInt32(0, 10001) / 10000.0;

        // Multiplier is between (1 - 0.20) = 0.80 and (1 + 0.20) = 1.20
        var multiplier = (1.0 - DEFAULT_JITTER_RATIO) + (2.0 * DEFAULT_JITTER_RATIO * randomSample);
        var ticks = (long)(baseDelay.Ticks * multiplier);
        return TimeSpan.FromTicks(Math.Max(0, ticks));
    }
}
