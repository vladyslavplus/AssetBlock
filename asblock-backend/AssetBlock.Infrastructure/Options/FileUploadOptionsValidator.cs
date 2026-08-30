using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class FileUploadOptionsValidator : IValidateOptions<FileUploadOptions>
{
    private const int MAX_EXTENSION_LENGTH = 32;

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
                if (string.IsNullOrWhiteSpace(ext))
                {
                    failures.Add("FileUpload:AllowedExtensions entry must not be empty or whitespace.");
                    continue;
                }

                if (ext.Length > MAX_EXTENSION_LENGTH)
                {
                    failures.Add($"FileUpload:AllowedExtensions entry '{ext}' exceeds maximum length of {MAX_EXTENSION_LENGTH} characters.");
                    continue;
                }

                if (!ext.StartsWith('.') || ext.EndsWith('.'))
                {
                    failures.Add($"FileUpload:AllowedExtensions entry '{ext}' must start with '.' and must not end with '.'.");
                    continue;
                }

                if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"FileUpload:AllowedExtensions must not include '{ext}' until a real safety fixture exists.");
                    continue;
                }

                // Check grammar: series of non-empty alphanumeric segments preceded by dots (e.g. .tar.gz, .zip)
                // Rejects controls, quotes, separators (/ \ :), Unicode, spaces, and empty segments (..)
                var segments = ext.Substring(1).Split('.');
                var hasInvalidSegment = false;
                foreach (var segment in segments)
                {
                    if (string.IsNullOrEmpty(segment))
                    {
                        failures.Add($"FileUpload:AllowedExtensions entry '{ext}' contains empty extension segments.");
                        break;
                    }

                    foreach (var c in segment)
                    {
                        var isAlphaNum = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
                        if (!isAlphaNum)
                        {
                            failures.Add($"FileUpload:AllowedExtensions entry '{ext}' contains invalid character '{c}'. Only ASCII alphanumeric characters and '.' are permitted.");
                            hasInvalidSegment = true;
                            break;
                        }
                    }

                    if (hasInvalidSegment)
                    {
                        break;
                    }
                }
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
