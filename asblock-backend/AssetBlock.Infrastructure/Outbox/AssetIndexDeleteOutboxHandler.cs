using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class AssetIndexDeleteOutboxHandler(
    IAssetSearchService searchService,
    ILogger<AssetIndexDeleteOutboxHandler> logger) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.ASSET_INDEX_DELETE;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<AssetIndexDeletePayload>(message.Payload, _json)
            ?? throw new InvalidOperationException("Invalid AssetIndexDelete payload.");

        await searchService.DeleteAsset(payload.AssetId, cancellationToken);
        logger.LogInformation("Deleted asset {AssetId} from search via outbox", payload.AssetId);
    }
}
