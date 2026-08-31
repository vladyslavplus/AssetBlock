using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.WebApi.IntegrationTests.Support;

/// <summary>
/// Analytics Redis/in-memory limiters use fixed windows keyed by Unix time.
/// Burst tests must start with enough remaining seconds so 120 sequential probes
/// cannot cross into the next bucket (CI flake → 121st request returns 202).
/// Call <see cref="EnsureWindowHasRemainingAsync"/> after hosts are started and
/// immediately before the first probe so host startup does not consume the margin.
/// </summary>
internal static class AnalyticsFixedWindowTestGuard
{
    /// <summary>
    /// Minimum remaining seconds in the current analytics-events window before a full-limit burst.
    /// Sized for ~120 HTTP probes under CI coverage/load after hosts are already running.
    /// Worst-case wait is just under this value (about 45s when the window is nearly over).
    /// </summary>
    private const int MIN_REMAINING_SECONDS_FOR_FULL_BURST = 45;

    public static async Task EnsureWindowHasRemainingAsync(
        int minRemainingSeconds = MIN_REMAINING_SECONDS_FOR_FULL_BURST,
        CancellationToken cancellationToken = default)
    {
        const int windowSeconds = RateLimitingConstants.Windows.ANALYTICS_EVENTS_PERIOD_SECONDS;
        if (minRemainingSeconds is <= 0 or >= windowSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minRemainingSeconds),
                minRemainingSeconds,
                $"Must be between 1 and {windowSeconds - 1}.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var remaining = windowSeconds - (int)(nowSec % windowSeconds);
            if (remaining >= minRemainingSeconds)
            {
                return;
            }

            // Land just after the next window boundary with a small cushion.
            var delayMs = (remaining * 1000) + 50;
            await Task.Delay(delayMs, cancellationToken);
        }
    }
}
