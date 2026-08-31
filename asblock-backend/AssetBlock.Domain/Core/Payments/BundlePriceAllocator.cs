using System.Numerics;

namespace AssetBlock.Domain.Core.Payments;

/// <summary>
/// Deterministic integer-cent allocation of a bundle price across ordered items.
/// Every line receives at least one cent; allocations sum exactly to the bundle total.
/// </summary>
public static class BundlePriceAllocator
{
    /// <summary>Stripe Checkout-compatible maximum amount in cents ($999,999.99).</summary>
    public const long MAX_AMOUNT_CENTS = 99_999_999L;

    /// <summary>Stripe Checkout-compatible maximum dollar amount.</summary>
    public const decimal MAX_AMOUNT = 999_999.99m;

    public sealed record AllocationInput(Guid AssetId, int Position, UsdAmount ListPrice);

    public sealed record AllocationResult(Guid AssetId, int Position, UsdAmount AllocatedPrice);

    /// <summary>
    /// Allocates <paramref name="bundleTotal"/> across items by list-price weight
    /// using floor division and largest-remainder distribution.
    /// Tie-break: higher fractional remainder first, then lower position, then lower AssetId.
    /// </summary>
    public static IReadOnlyList<AllocationResult> Allocate(
        UsdAmount bundleTotal,
        IReadOnlyList<AllocationInput> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            throw new ArgumentException("At least one item is required.", nameof(items));
        }

        var bundleTotalCents = bundleTotal.Cents;
        if (bundleTotalCents < items.Count || bundleTotalCents > MAX_AMOUNT_CENTS)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bundleTotal),
                $"Bundle total must be between {items.Count} and {MAX_AMOUNT_CENTS} cents.");
        }

        var seenAssets = new HashSet<Guid>();
        long listTotal = 0;
        foreach (var item in items)
        {
            if (item.Position <= 0)
            {
                throw new ArgumentException("Item positions must be positive.", nameof(items));
            }

            if (item.ListPrice.Cents is <= 0 or > MAX_AMOUNT_CENTS)
            {
                throw new ArgumentException(
                    $"Item list prices must be between 1 and {MAX_AMOUNT_CENTS} cents.",
                    nameof(items));
            }

            if (!seenAssets.Add(item.AssetId))
            {
                throw new ArgumentException("Item asset ids must be distinct.", nameof(items));
            }

            listTotal = checked(listTotal + item.ListPrice.Cents);
        }

        // Reserve one cent per line, then distribute the remainder proportionally.
        var reserved = items.Count;
        var remainingToDistribute = bundleTotalCents - reserved;

        var ordered = items
            .OrderBy(i => i.Position)
            .ThenBy(i => i.AssetId)
            .ToArray();

        var baseAllocations = new long[ordered.Length];
        var remainders = new (int Index, long Remainder, int Position, Guid AssetId)[ordered.Length];

        for (var i = 0; i < ordered.Length; i++)
        {
            // BigInteger avoids overflow when multiplying large long cents.
            var weighted = (BigInteger)remainingToDistribute * ordered[i].ListPrice.Cents;
            var baseShare = (long)(weighted / listTotal);
            var remainder = (long)(weighted % listTotal);
            baseAllocations[i] = baseShare;
            remainders[i] = (i, remainder, ordered[i].Position, ordered[i].AssetId);
        }

        var distributedBase = baseAllocations.Sum();
        var leftover = remainingToDistribute - distributedBase;

        Array.Sort(remainders, static (a, b) =>
        {
            var cmp = b.Remainder.CompareTo(a.Remainder);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.Position.CompareTo(b.Position);
            if (cmp != 0)
            {
                return cmp;
            }

            return a.AssetId.CompareTo(b.AssetId);
        });

        for (var i = 0; i < leftover; i++)
        {
            baseAllocations[remainders[i].Index]++;
        }

        var results = new AllocationResult[ordered.Length];
        long sum = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var allocatedCents = checked(baseAllocations[i] + 1); // include reserved cent
            sum = checked(sum + allocatedCents);
            results[i] = new AllocationResult(ordered[i].AssetId, ordered[i].Position, UsdAmount.FromCents(allocatedCents));
        }

        if (sum != bundleTotalCents)
        {
            throw new InvalidOperationException("Bundle price allocation did not sum to the bundle total.");
        }

        return results;
    }
}
