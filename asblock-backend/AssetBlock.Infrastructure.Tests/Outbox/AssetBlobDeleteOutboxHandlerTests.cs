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
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly AssetBlobDeleteOutboxHandler _sut;

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AssetBlobDeleteOutboxHandlerTests()
    {
        _assetStore.CanDeleteRetiredUploadBlob(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new AssetBlobDeleteOutboxHandler(
            _storageService,
            _assetStore,
            NullLogger<AssetBlobDeleteOutboxHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenStorageKeyHasBeenReferencedAgain_ShouldSkipDeletion()
    {
        const string storageKey = "assets/reused-key.bin";
        const string versionId = "version-1";
        _assetStore.CanDeleteRetiredUploadBlob(
            Arg.Any<Guid>(), Arg.Any<Guid>(), storageKey, versionId, Arg.Any<CancellationToken>()).Returns(false);
        OutboxMessage message = CreateMessage(JsonSerializer.Serialize(
            new AssetBlobDeletePayload(Guid.NewGuid(), Guid.NewGuid(), storageKey, versionId), _json));

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Asset blob deletion is deferred while relational protection or exact identity checks fail.");

        await _assetStore.Received(1).CanDeleteRetiredUploadBlob(
            Arg.Any<Guid>(), Arg.Any<Guid>(), storageKey, versionId, Arg.Any<CancellationToken>());
        await _storageService.DidNotReceiveWithAnyArgs().Delete(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenValidPayload_ShouldForwardExactStorageKey()
    {
        var assetId = Guid.NewGuid();
        const string storageKey = "assets/test-asset-key/v1.bin";
        const string versionId = "version-2";
        var payload = new AssetBlobDeletePayload(assetId, Guid.NewGuid(), storageKey, versionId);
        OutboxMessage message = CreateMessage(JsonSerializer.Serialize(payload, _json));

        await _sut.Handle(message, CancellationToken.None);

        await _storageService.Received(1).Delete(storageKey, versionId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPayloadOmitsExactIdentity_ShouldFailClosed()
    {
        OutboxMessage message = CreateMessage("{\"assetId\":\"b3df7514-8d21-43c4-b278-c2e6b46c47a6\",\"storageKey\":\"assets/legacy.bin\"}");

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Asset blob deletion requires an exact provider object version ID.");
        await _storageService.DidNotReceiveWithAnyArgs().Delete(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenExactTargetIdentityIsIncomplete_ShouldFailClosedBeforeProtectionLookup()
    {
        AssetBlobDeletePayload payload = new(Guid.Empty, Guid.NewGuid(), "assets/invalid.bin", "version-1");
        OutboxMessage message = CreateMessage(JsonSerializer.Serialize(payload, _json));

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Asset blob deletion requires an exact provider object version ID.");
        await _assetStore.DidNotReceiveWithAnyArgs().CanDeleteRetiredUploadBlob(
            Guid.Empty, Guid.Empty, null!, null!, CancellationToken.None);
        await _storageService.DidNotReceiveWithAnyArgs().Delete(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenMalformedJson_ShouldThrowAndNeverCallStorage()
    {
        OutboxMessage message = CreateMessage("this is not valid json");

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
        await _storageService.DidNotReceiveWithAnyArgs().Delete(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenNullJson_ShouldThrowInvalidOperationAndNeverCallStorage()
    {
        OutboxMessage message = CreateMessage("null");

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid AssetBlobDelete payload.");
        await _storageService.DidNotReceiveWithAnyArgs().Delete(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenStorageFails_ShouldPropagateExceptionForDispatcherRetry()
    {
        var assetId = Guid.NewGuid();
        const string storageKey = "assets/failing-key.bin";
        const string versionId = "version-3";
        var payload = new AssetBlobDeletePayload(assetId, Guid.NewGuid(), storageKey, versionId);
        OutboxMessage message = CreateMessage(JsonSerializer.Serialize(payload, _json));

        _storageService.Delete(storageKey, versionId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Storage gateway timeout"));

        Func<Task> act = () => _sut.Handle(message, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("Storage gateway timeout");
        await _storageService.Received(1).Delete(storageKey, versionId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCalledTwice_ShouldForwardTwoDeletesRelyingOnStorageIdempotence()
    {
        var assetId = Guid.NewGuid();
        const string storageKey = "assets/idempotent-key.bin";
        const string versionId = "version-4";
        var payload = new AssetBlobDeletePayload(assetId, Guid.NewGuid(), storageKey, versionId);
        OutboxMessage message = CreateMessage(JsonSerializer.Serialize(payload, _json));

        await _sut.Handle(message, CancellationToken.None);
        await _sut.Handle(message, CancellationToken.None);

        await _storageService.Received(2).Delete(storageKey, versionId, Arg.Any<CancellationToken>());
    }

    private static OutboxMessage CreateMessage(string payload) => new()
    {
        Id = Guid.NewGuid(),
        Type = OutboxMessageTypes.ASSET_BLOB_DELETE,
        Payload = payload,
        OccurredAt = DateTimeOffset.UtcNow
    };
}
