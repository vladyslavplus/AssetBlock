using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Infrastructure.Tests.Outbox;

public sealed class AssetBlobDeleteOutboxHandlerTests
{
    private readonly IAssetStorageService _storageService = Substitute.For<IAssetStorageService>();
    private readonly AssetBlobDeleteOutboxHandler _sut;

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AssetBlobDeleteOutboxHandlerTests()
    {
        _sut = new AssetBlobDeleteOutboxHandler(
            _storageService,
            NullLogger<AssetBlobDeleteOutboxHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenValidPayload_ShouldForwardExactStorageKey()
    {
        var assetId = Guid.NewGuid();
        const string storageKey = "assets/test-asset-key/v1.bin";
        var payload = new AssetBlobDeletePayload(assetId, storageKey);
        OutboxMessage message = CreateMessage(JsonSerializer.Serialize(payload, _json));

        await _sut.Handle(message, CancellationToken.None);

        await _storageService.Received(1).Delete(storageKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenMalformedJson_ShouldThrowAndNeverCallStorage()
    {
        OutboxMessage message = CreateMessage("this is not valid json");

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
        await _storageService.DidNotReceiveWithAnyArgs().Delete(null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenNullJson_ShouldThrowInvalidOperationAndNeverCallStorage()
    {
        OutboxMessage message = CreateMessage("null");

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid AssetBlobDelete payload.");
        await _storageService.DidNotReceiveWithAnyArgs().Delete(null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenStorageFails_ShouldPropagateExceptionForDispatcherRetry()
    {
        var assetId = Guid.NewGuid();
        const string storageKey = "assets/failing-key.bin";
        var payload = new AssetBlobDeletePayload(assetId, storageKey);
        OutboxMessage message = CreateMessage(JsonSerializer.Serialize(payload, _json));

        _storageService.Delete(storageKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Storage gateway timeout"));

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("Storage gateway timeout");
        await _storageService.Received(1).Delete(storageKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCalledTwice_ShouldForwardTwoDeletesRelyingOnStorageIdempotence()
    {
        var assetId = Guid.NewGuid();
        const string storageKey = "assets/idempotent-key.bin";
        var payload = new AssetBlobDeletePayload(assetId, storageKey);
        OutboxMessage message = CreateMessage(JsonSerializer.Serialize(payload, _json));

        await _sut.Handle(message, CancellationToken.None);
        await _sut.Handle(message, CancellationToken.None);

        await _storageService.Received(2).Delete(storageKey, Arg.Any<CancellationToken>());
    }

    private static OutboxMessage CreateMessage(string payload) => new()
    {
        Id = Guid.NewGuid(),
        Type = OutboxMessageTypes.ASSET_BLOB_DELETE,
        Payload = payload,
        OccurredAt = DateTimeOffset.UtcNow
    };
}
