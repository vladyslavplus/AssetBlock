namespace AssetBlock.Domain.Core.Payments;

/// <summary>ISO 4217 alphabetic currency codes as three lowercase ASCII letters.</summary>
public static class IsoCurrency
{
    private const int CODE_LENGTH = 3;

    /// <summary>
    /// Accepts only exactly three ASCII letters; returns lowercase code.
    /// Does not accept whitespace, digits, symbols, or mixed-length values.
    /// </summary>
    public static bool TryNormalize(string? raw, out string currency)
    {
        if (raw is null || raw.Length != CODE_LENGTH)
        {
            currency = string.Empty;
            return false;
        }

        var c0 = raw[0];
        var c1 = raw[1];
        var c2 = raw[2];
        if (!IsAsciiLetter(c0) || !IsAsciiLetter(c1) || !IsAsciiLetter(c2))
        {
            currency = string.Empty;
            return false;
        }

        currency = string.Create(CODE_LENGTH, raw, static (span, source) =>
        {
            span[0] = char.ToLowerInvariant(source[0]);
            span[1] = char.ToLowerInvariant(source[1]);
            span[2] = char.ToLowerInvariant(source[2]);
        });
        return true;
    }

    private static bool IsAsciiLetter(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
