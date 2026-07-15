using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

namespace AssetBlock.WebApi.Extensions;

public static class FileUploadExtensions
{
    internal const long MULTIPART_OVERHEAD_BYTES = 1024 * 1024;

    /// <summary>
    /// Configures Kestrel and multipart form limits from <see cref="FileUploadOptions"/>.
    /// MemoryBufferThreshold is kept low so large uploads are disk-buffered and seekable.
    /// </summary>
    public static IServiceCollection AddFileUploadLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(FileUploadOptions.SECTION_NAME);
        var maxFileBytes = section.GetValue<long?>(nameof(FileUploadOptions.MaxFileBytes))
            ?? new FileUploadOptions().MaxFileBytes;
        var maxRequestBytes = checked(maxFileBytes + MULTIPART_OVERHEAD_BYTES);

        services.Configure<FormOptions>(options =>
        {
            // The multipart body also contains boundaries and the asset metadata fields.
            options.MultipartBodyLengthLimit = maxRequestBytes;
            // Buffer small parts in memory; larger bodies spill to disk (seekable temp files).
            options.MemoryBufferThreshold = 64 * 1024;
        });

        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = maxRequestBytes;
        });

        return services;
    }
}
