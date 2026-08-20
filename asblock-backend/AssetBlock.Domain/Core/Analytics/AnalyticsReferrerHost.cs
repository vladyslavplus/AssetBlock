using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Domain.Core.Analytics;

/// <summary>
/// Reduces an untrusted referrer value to a bare lowercase ASCII host suitable for grouping and storage.
/// Paths, queries, and userinfo are dropped rather than sanitized so no visitor-identifying fragment
/// can reach the database.
/// </summary>
public static class AnalyticsReferrerHost
{
    private const int MAX_LABEL_LENGTH = 63;

    /// <summary>
    /// Strips scheme, userinfo, port, path, query, and fragment, then lowercases the remaining host.
    /// Returns null when the input is absent or is not a syntactically valid ASCII host.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();

        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            value = value[(schemeIndex + 3)..];
        }

        var authorityEnd = value.AsSpan().IndexOfAny('/', '?', '#');
        if (authorityEnd >= 0)
        {
            value = value[..authorityEnd];
        }

        var userInfoIndex = value.LastIndexOf('@');
        if (userInfoIndex >= 0)
        {
            value = value[(userInfoIndex + 1)..];
        }

        var portIndex = value.LastIndexOf(':');
        if (portIndex >= 0)
        {
            value = value[..portIndex];
        }

        if (value.Length == 0 || value.Length > AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH)
        {
            return null;
        }

        return IsValidHost(value) ? value.ToLowerInvariant() : null;
    }

    private static bool IsValidHost(string host)
    {
        if (host[0] is '.' or '-' || host[^1] is '.' or '-')
        {
            return false;
        }

        var labelLength = 0;
        foreach (var c in host)
        {
            if (c == '.')
            {
                if (labelLength == 0)
                {
                    return false;
                }

                labelLength = 0;
                continue;
            }

            if (!IsHostChar(c))
            {
                return false;
            }

            labelLength++;
            if (labelLength > MAX_LABEL_LENGTH)
            {
                return false;
            }
        }

        return labelLength > 0;
    }

    private static bool IsHostChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-';
}
