namespace AssetBlock.WebApi.Extensions;

public static class HostFilteringExtensions
{
    /// <summary>
    /// Validates and registers host filtering configuration.
    /// In non-Development environments, <c>AllowedHosts</c> must be explicitly configured and must not be or contain wildcard ('*').
    /// In Development or IntegrationTesting environments, missing, empty, or wildcard AllowedHosts is permitted for local dev convenience.
    /// </summary>
    public static IServiceCollection AddAssetBlockHostFiltering(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var allowedHosts = configuration["AllowedHosts"]?.Trim();

        if (environment.IsDevelopment() || environment.IsEnvironment("IntegrationTesting"))
        {
            return services;
        }

        if (string.IsNullOrWhiteSpace(allowedHosts))
        {
            throw new InvalidOperationException(
                "Configuration 'AllowedHosts' must be explicitly configured in non-Development environments.");
        }

        var hosts = allowedHosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(h => h.Contains('*')))
        {
            throw new InvalidOperationException(
                "Configuration 'AllowedHosts' must not contain wildcard ('*') in non-Development environments.");
        }

        return services;
    }
}
