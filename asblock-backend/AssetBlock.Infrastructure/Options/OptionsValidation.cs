namespace AssetBlock.Infrastructure.Options;

internal static class OptionsValidation
{
    /// <summary>
    /// True when the value is null/whitespace or a tracked-config placeholder like &lt;name&gt;
    /// (including forms such as &lt;minio-endpoint&gt;:9000).
    /// </summary>
    public static bool IsMissingOrPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed[0] != '<')
        {
            return false;
        }

        var close = trimmed.IndexOf('>');
        return close > 0;
    }

    public static bool IsAbsoluteHttpUri(string? value)
    {
        if (IsMissingOrPlaceholder(value))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// True for absolute http(s) origin only: no user-info, query, fragment, or non-root path.
    /// </summary>
    public static bool IsHttpOrigin(string? value)
    {
        if (IsMissingOrPlaceholder(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(uri.Host);
    }

    /// <summary>
    /// Validates an S3-compatible endpoint as host:port or absolute http(s) URI.
    /// Absolute URIs must agree with UseSsl and must not include user-info, path, query, or fragment.
    /// </summary>
    /// <param name="configPrefix">Options path prefix used in error messages (e.g. Minio or SeaweedFs).</param>
    public static bool TryValidateS3CompatibleEndpoint(
        string? endpoint,
        bool useSsl,
        string configPrefix,
        out string? error)
    {
        error = null;

        if (IsMissingOrPlaceholder(endpoint))
        {
            error = $"{configPrefix}:Endpoint must be non-empty.";
            return false;
        }

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            if (!TryRejectEndpointExtras(absolute, configPrefix, out error))
            {
                return false;
            }

            var requiresSsl = absolute.Scheme == Uri.UriSchemeHttps;
            if (useSsl != requiresSsl)
            {
                error = requiresSsl
                    ? $"{configPrefix}:UseSsl must be true when Endpoint uses the https scheme."
                    : $"{configPrefix}:UseSsl must be false when Endpoint uses the http scheme.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(absolute.Host))
            {
                error = $"{configPrefix}:Endpoint host is invalid.";
                return false;
            }

            return true;
        }

        if (endpoint!.Contains("://", StringComparison.Ordinal))
        {
            error = $"{configPrefix}:Endpoint must be a host:port value or an absolute http/https URI.";
            return false;
        }

        if (!Uri.TryCreate($"http://{endpoint}", UriKind.Absolute, out Uri? hostPortUri)
            || string.IsNullOrWhiteSpace(hostPortUri.Host)
            || hostPortUri.Host.Contains(' ', StringComparison.Ordinal))
        {
            error = $"{configPrefix}:Endpoint host:port value is invalid.";
            return false;
        }

        if (!TryRejectEndpointExtras(hostPortUri, configPrefix, out error))
        {
            return false;
        }

        // Reject values where the port suffix is present but not a valid TCP port.
        var separator = endpoint.LastIndexOf(':');
        if (separator > 0)
        {
            var portPart = endpoint[(separator + 1)..];
            if (portPart.Length > 0
                && (!int.TryParse(portPart, out var port) || port is <= 0 or > 65535))
            {
                error = $"{configPrefix}:Endpoint port is invalid.";
                return false;
            }
        }

        return true;
    }

    private static bool TryRejectEndpointExtras(Uri uri, string configPrefix, out string? error)
    {
        error = null;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = $"{configPrefix}:Endpoint must not include user info.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            error = $"{configPrefix}:Endpoint must not include a path.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            error = $"{configPrefix}:Endpoint must not include a query string.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            error = $"{configPrefix}:Endpoint must not include a fragment.";
            return false;
        }

        return true;
    }
}
