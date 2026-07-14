using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class ElasticsearchOptionsValidator : IValidateOptions<ElasticsearchOptions>
{
    public ValidateOptionsResult Validate(string? name, ElasticsearchOptions options)
    {
        var failures = new List<string>();

        if (OptionsValidation.IsMissingOrPlaceholder(options.Url)
            || !OptionsValidation.IsAbsoluteHttpUri(options.Url))
        {
            failures.Add("Elasticsearch:Url must be an absolute http or https URI.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.DefaultIndex))
        {
            failures.Add("Elasticsearch:DefaultIndex must be non-empty.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
