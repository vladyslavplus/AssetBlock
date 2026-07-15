using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class FileUploadOptionsValidator : IValidateOptions<FileUploadOptions>
{
    public ValidateOptionsResult Validate(string? name, FileUploadOptions options)
    {
        var failures = new List<string>();

        if (options.MaxFileBytes <= 0)
        {
            failures.Add("FileUpload:MaxFileBytes must be greater than zero.");
        }

        if (options.AllowedExtensions.Length == 0)
        {
            failures.Add("FileUpload:AllowedExtensions must contain at least one extension.");
        }
        else
        {
            foreach (var ext in options.AllowedExtensions)
            {
                if (string.IsNullOrWhiteSpace(ext) || !ext.StartsWith('.'))
                {
                    failures.Add($"FileUpload:AllowedExtensions entry '{ext}' must be a non-empty suffix starting with '.'.");
                }
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
