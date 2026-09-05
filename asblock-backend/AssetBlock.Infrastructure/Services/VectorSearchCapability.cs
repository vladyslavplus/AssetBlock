using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

public sealed class VectorSearchCapability(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IOptions<EmbeddingOptions> options,
    ILogger<VectorSearchCapability> logger) : IVectorSearchCapability
{
    public async Task<VectorSearchCapabilityResult> CheckCapability(CancellationToken cancellationToken = default)
    {
        EmbeddingOptions opt = options.Value;
        if (!opt.Enabled)
        {
            return VectorSearchCapabilityResult.Disabled();
        }

        var modelKey = EmbeddingModelKey.Compute(opt);

        try
        {
            await using ApplicationDbContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var hasExtension = await context.Database
                .SqlQueryRaw<int>("""SELECT 1 AS "Value" FROM pg_extension WHERE extname = 'vector' LIMIT 1;""")
                .AnyAsync(cancellationToken);

            if (!hasExtension)
            {
                logger.LogWarning("Vector search capability unavailable: vector extension not found in database.");
                return VectorSearchCapabilityResult.ExtensionMissing(modelKey);
            }

            return VectorSearchCapabilityResult.Available(modelKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Vector search capability probe failed due to database error.");
            return new VectorSearchCapabilityResult(false, true, false, modelKey, ex.Message);
        }
    }

    public async Task<bool> IsVectorSearchAvailable(CancellationToken cancellationToken = default)
    {
        return (await CheckCapability(cancellationToken)).IsAvailable;
    }
}
