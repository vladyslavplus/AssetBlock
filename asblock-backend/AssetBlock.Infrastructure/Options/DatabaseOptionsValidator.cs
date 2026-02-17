using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        if (options is { AutoMigrate: true, EnsureCreated: true })
        {
            return ValidateOptionsResult.Fail(
                "Database: AutoMigrate and EnsureCreated cannot both be true. Use one; EnsureCreated takes precedence if both are set.");
        }
        return ValidateOptionsResult.Success;
    }
}
