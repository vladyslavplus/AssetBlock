using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class ArchiveInspectionOptionsValidator : IValidateOptions<ArchiveInspectionOptions>
{
    public ValidateOptionsResult Validate(string? name, ArchiveInspectionOptions options)
    {
        var errors = new List<string>();

        if (options.MaxEntries is <= 0 or > ArchiveInspectionOptions.MAX_ENTRIES_UPPER)
        {
            errors.Add($"ArchiveInspection:MaxEntries must be between 1 and {ArchiveInspectionOptions.MAX_ENTRIES_UPPER}.");
        }

        if (options.MaxTotalExpandedBytes is <= 0 or > ArchiveInspectionOptions.MAX_TOTAL_EXPANDED_BYTES_UPPER)
        {
            errors.Add($"ArchiveInspection:MaxTotalExpandedBytes must be between 1 and {ArchiveInspectionOptions.MAX_TOTAL_EXPANDED_BYTES_UPPER}.");
        }

        if (options.MaxEntryExpandedBytes is <= 0 or > ArchiveInspectionOptions.MAX_TOTAL_EXPANDED_BYTES_UPPER)
        {
            errors.Add("ArchiveInspection:MaxEntryExpandedBytes must be a positive finite value within the total expanded-size upper bound.");
        }

        if (options.MaxEntryExpandedBytes > options.MaxTotalExpandedBytes)
        {
            errors.Add("ArchiveInspection:MaxEntryExpandedBytes cannot be greater than MaxTotalExpandedBytes.");
        }

        if (!double.IsFinite(options.MaxCompressionRatio)
            || options.MaxCompressionRatio <= 0
            || options.MaxCompressionRatio > ArchiveInspectionOptions.MAX_COMPRESSION_RATIO_UPPER)
        {
            errors.Add($"ArchiveInspection:MaxCompressionRatio must be a finite value between 0 (exclusive) and {ArchiveInspectionOptions.MAX_COMPRESSION_RATIO_UPPER}.");
        }

        if (options.MaxPathLength is <= 0 or > ArchiveInspectionOptions.MAX_PATH_LENGTH_UPPER)
        {
            errors.Add($"ArchiveInspection:MaxPathLength must be between 1 and {ArchiveInspectionOptions.MAX_PATH_LENGTH_UPPER}.");
        }

        if (options.MaxPathDepth is <= 0 or > ArchiveInspectionOptions.MAX_PATH_DEPTH_UPPER)
        {
            errors.Add($"ArchiveInspection:MaxPathDepth must be between 1 and {ArchiveInspectionOptions.MAX_PATH_DEPTH_UPPER}.");
        }

        if (options.MaxReadmeBytes <= 0 || options.MaxReadmeBytes > 16384)
        {
            errors.Add("ArchiveInspection:MaxReadmeBytes must be between 1 and 16384.");
        }

        if (options.MaxManifestFiles is <= 0 or > ArchiveInspectionOptions.MAX_MANIFEST_FILES_UPPER)
        {
            errors.Add($"ArchiveInspection:MaxManifestFiles must be between 1 and {ArchiveInspectionOptions.MAX_MANIFEST_FILES_UPPER}.");
        }

        if (options.MaxManifestFiles > options.MaxEntries)
        {
            errors.Add("ArchiveInspection:MaxManifestFiles cannot be greater than MaxEntries.");
        }

        if (options.MaxManifestBytes <= 0 || options.MaxManifestBytes > 16384)
        {
            errors.Add("ArchiveInspection:MaxManifestBytes must be between 1 and 16384.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
