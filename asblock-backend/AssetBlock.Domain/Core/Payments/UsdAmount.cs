namespace AssetBlock.Domain.Core.Payments;

/// <summary>
/// Canonical USD monetary value represented internally as non-negative integer cents.
/// </summary>
public readonly record struct UsdAmount
{
    private const decimal MAX_DOLLARS_BEFORE_CENTS_OVERFLOW = (decimal)long.MaxValue / 100m;

    public long Cents { get; }

    public decimal Dollars => Cents / 100m;

    private UsdAmount(long cents)
    {
        Cents = cents;
    }

    /// <summary>
    /// Constructs a <see cref="UsdAmount"/> from non-negative integer cents.
    /// </summary>
    public static UsdAmount FromCents(long cents)
    {
        if (cents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cents), cents, "Cents cannot be negative.");
        }

        return new UsdAmount(cents);
    }

    /// <summary>
    /// Constructs a <see cref="UsdAmount"/> from a decimal dollar amount with exact cent precision (at most two decimal places).
    /// Rejects negative amounts, values exceeding maximum range, and sub-cent precision.
    /// </summary>
    public static UsdAmount FromDollarsExact(decimal dollars)
    {
        if (dollars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dollars), dollars, "Dollar amount cannot be negative.");
        }

        if (dollars > MAX_DOLLARS_BEFORE_CENTS_OVERFLOW)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dollars),
                dollars,
                $"Dollar amount exceeds the maximum supported value of {MAX_DOLLARS_BEFORE_CENTS_OVERFLOW}.");
        }

        if (!HasAtMostTwoDecimalPlaces(dollars))
        {
            throw new ArgumentException("Amount must have at most two decimal places.", nameof(dollars));
        }

        var cents = (long)(dollars * 100m);
        return new UsdAmount(cents);
    }

    /// <summary>
    /// Constructs a <see cref="UsdAmount"/> from a decimal dollar amount, rounding to integer cents
    /// using the explicitly supplied <paramref name="midpointRounding"/> mode.
    /// </summary>
    public static UsdAmount FromDollarsRounded(decimal dollars, MidpointRounding midpointRounding)
    {
        if (dollars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dollars), dollars, "Dollar amount cannot be negative.");
        }

        if (dollars > MAX_DOLLARS_BEFORE_CENTS_OVERFLOW + 0.005m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dollars),
                dollars,
                $"Dollar amount exceeds the maximum supported value of {MAX_DOLLARS_BEFORE_CENTS_OVERFLOW}.");
        }

        var scaledCents = decimal.Round(dollars * 100m, 0, midpointRounding);
        if (scaledCents > (decimal)long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dollars),
                dollars,
                $"Dollar amount exceeds the maximum supported value of {MAX_DOLLARS_BEFORE_CENTS_OVERFLOW}.");
        }

        return new UsdAmount((long)scaledCents);
    }

    /// <summary>
    /// Checks whether the dollar amount has at most two decimal places.
    /// </summary>
    public static bool HasAtMostTwoDecimalPlaces(decimal dollars)
    {
        return decimal.Round(dollars, 2, MidpointRounding.AwayFromZero) == dollars;
    }

    public override string ToString() => $"${Dollars:F2}";
}
