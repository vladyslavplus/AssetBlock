using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class AssetBlobDeleteOutboxHandler(
    IAssetStorageService storageService,
    ILogger<AssetBlobDeleteOutboxHandler> logger) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.ASSET_BLOB_DELETE;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<AssetBlobDeletePayload>(message.Payload, _json)
            ?? throw new InvalidOperationException("Invalid AssetBlobDelete payload.");

        await storageService.Delete(payload.StorageKey, cancellationToken);
        logger.LogInformation("Deleted blob {StorageKey} for asset {AssetId} via outbox", payload.StorageKey, payload.AssetId);
    }
}
