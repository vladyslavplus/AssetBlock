using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            errors.Add("ServiceName is required when Observability is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.OtlpEndpoint)
            || !Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out Uri? result)
            || (result.Scheme != Uri.UriSchemeHttp && result.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("OtlpEndpoint must be a valid absolute HTTP/HTTPS URI when Observability is enabled.");
        }

        if (!double.IsFinite(options.TraceSampleRatio) || options.TraceSampleRatio is < 0.0 or > 1.0)
        {
            errors.Add("TraceSampleRatio must be between 0.0 and 1.0.");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
