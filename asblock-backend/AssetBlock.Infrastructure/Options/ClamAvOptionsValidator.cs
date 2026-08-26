using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class ClamAvOptionsValidator(IConfiguration configuration) : IValidateOptions<ClamAvOptions>
{
    public ValidateOptionsResult Validate(string? name, ClamAvOptions options)
    {
        var processingEnabled = configuration.GetValue($"{AssetProcessingOptions.SECTION_NAME}:Enabled", false);
        var errors = new List<string>();

        if (processingEnabled && !options.Enabled)
        {
            errors.Add("ClamAv:Enabled must be true when AssetProcessing:Enabled is true.");
        }

        if (!options.Enabled && !processingEnabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            errors.Add("ClamAv:Host must be a non-empty hostname or IP address.");
        }

        if (options.Port is <= 0 or > 65535)
        {
            errors.Add("ClamAv:Port must be between 1 and 65535.");
        }

        if (options.ConnectTimeoutMs is < ClamAvOptions.MIN_TIMEOUT_MS or > ClamAvOptions.MAX_CONNECT_TIMEOUT_MS)
        {
            errors.Add($"ClamAv:ConnectTimeoutMs must be between {ClamAvOptions.MIN_TIMEOUT_MS} and {ClamAvOptions.MAX_CONNECT_TIMEOUT_MS}.");
        }

        if (options.ReadTimeoutMs is < ClamAvOptions.MIN_TIMEOUT_MS or > ClamAvOptions.MAX_IO_TIMEOUT_MS)
        {
            errors.Add($"ClamAv:ReadTimeoutMs must be between {ClamAvOptions.MIN_TIMEOUT_MS} and {ClamAvOptions.MAX_IO_TIMEOUT_MS}.");
        }

        if (options.WriteTimeoutMs is < ClamAvOptions.MIN_TIMEOUT_MS or > ClamAvOptions.MAX_IO_TIMEOUT_MS)
        {
            errors.Add($"ClamAv:WriteTimeoutMs must be between {ClamAvOptions.MIN_TIMEOUT_MS} and {ClamAvOptions.MAX_IO_TIMEOUT_MS}.");
        }

        var combinedTimeoutMs = (long)options.ConnectTimeoutMs + options.ReadTimeoutMs;
        if (combinedTimeoutMs > int.MaxValue)
        {
            errors.Add("ClamAv:ConnectTimeoutMs + ReadTimeoutMs must fit in a 32-bit millisecond timeout.");
        }

        if (options.MaxStreamBytes is <= 0 or > ClamAvOptions.MAX_STREAM_BYTES)
        {
            errors.Add($"ClamAv:MaxStreamBytes must be between 1 and {ClamAvOptions.MAX_STREAM_BYTES}.");
        }

        var maxFileBytes = configuration.GetValue<long?>($"{FileUploadOptions.SECTION_NAME}:MaxFileBytes")
            ?? new FileUploadOptions().MaxFileBytes;
        if (maxFileBytes <= 0)
        {
            maxFileBytes = new FileUploadOptions().MaxFileBytes;
        }

        if (options.MaxStreamBytes < maxFileBytes)
        {
            errors.Add("ClamAv:MaxStreamBytes must be greater than or equal to FileUpload:MaxFileBytes.");
        }

        if (options.DaemonMaxStreamBytes is < 0 or > ClamAvOptions.MAX_STREAM_BYTES)
        {
            errors.Add($"ClamAv:DaemonMaxStreamBytes must be between 0 and {ClamAvOptions.MAX_STREAM_BYTES}.");
        }

        if (options.MaxSignatureAge < ClamAvOptions.MinSignatureAge
            || options.MaxSignatureAge > ClamAvOptions.MaxSignatureAgAge)
        {
            errors.Add("ClamAv:MaxSignatureAge must be between 1 hour and 7 days.");
        }

        if (options.MaxResponseBytes is <= 0 or > 64 * 1024)
        {
            errors.Add("ClamAv:MaxResponseBytes must be between 1 and 65536.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
