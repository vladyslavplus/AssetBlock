using AssetBlock.Application.UseCases.Assets.DeleteAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class DeleteAssetCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly IPurchaseStore _purchaseStoreMock;
    private readonly ICheckoutIntentStore _checkoutIntentStoreMock;
    private readonly IOutboxStore _outboxStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteAssetCommandHandler _handler;

    public DeleteAssetCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _outboxStoreMock = Substitute.For<IOutboxStore>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();
        _purchaseStoreMock.HasPurchasesForAsset(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _checkoutIntentStoreMock.HasActiveForAsset(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new DeleteAssetCommandHandler(
            _assetStoreMock,
            _purchaseStoreMock,
            _checkoutIntentStoreMock,
            unitOfWorkMock,
            _outboxStoreMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<DeleteAssetCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new DeleteAssetCommand(Guid.NewGuid(), Guid.NewGuid());
        _assetStoreMock.GetById(command.Id).Returns((Asset?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbiddenAndWriteDeniedAudit()
    {
        var command = new DeleteAssetCommand(Guid.NewGuid(), Guid.NewGuid());
        var asset = new Asset { Id = command.Id, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.Id).Returns(asset);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_DELETE &&
                e.Outcome == AuditOutcome.DENIED &&
                e.ResourceType == AuditResourceTypes.ASSET &&
                e.ResourceId == command.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSuccess_ShouldHardDeleteEnqueueBlobOutboxAuditAndClearCache()
    {
        var authorId = Guid.NewGuid();
        var command = new DeleteAssetCommand(Guid.NewGuid(), authorId);
        var asset = new Asset { Id = command.Id, AuthorId = authorId, StorageKey = "key", CategoryId = Guid.NewGuid(), Title = "t", FileName = "f" };
        _assetStoreMock.GetById(command.Id).Returns(asset);
        _assetStoreMock.GetForUpdate(command.Id, Arg.Any<CancellationToken>()).Returns(asset);
        _assetStoreMock.GetAllStorageKeys(command.Id, Arg.Any<CancellationToken>())
            .Returns(["key"]);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _purchaseStoreMock.Received(1).HasPurchasesForAsset(command.Id, Arg.Any<CancellationToken>());
        await _assetStoreMock.Received(1).Delete(command.Id);
        await _assetStoreMock.DidNotReceive().SoftDelete(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.ASSET_BLOB_DELETE,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_HARD_DELETE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == asset.Id.ToString()),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX);
    }

    [Fact]
    public async Task Handle_WhenPurchasesExist_ShouldSoftDeleteWithAuditWithoutOutbox()
    {
        var authorId = Guid.NewGuid();
        var command = new DeleteAssetCommand(Guid.NewGuid(), authorId);
        var asset = new Asset { Id = command.Id, AuthorId = authorId, StorageKey = "key", CategoryId = Guid.NewGuid(), Title = "t", FileName = "f" };
        _assetStoreMock.GetById(command.Id).Returns(asset);
        _assetStoreMock.GetForUpdate(command.Id, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.HasPurchasesForAsset(command.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).SoftDelete(command.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _assetStoreMock.DidNotReceive().Delete(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceiveWithAnyArgs().Enqueue(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_SOFT_DELETE &&
                e.Outcome == AuditOutcome.SUCCESS),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX);
    }

    [Fact]
    public async Task Handle_WhenAlreadyDelisted_ShouldReturnSuccessWithoutMutating()
    {
        var authorId = Guid.NewGuid();
        var command = new DeleteAssetCommand(Guid.NewGuid(), authorId);
        var asset = new Asset
        {
            Id = command.Id,
            AuthorId = authorId,
            StorageKey = "key",
            CategoryId = Guid.NewGuid(),
            Title = "t",
            FileName = "f",
            DeletedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        _assetStoreMock.GetById(command.Id).Returns(asset);
        _assetStoreMock.GetForUpdate(command.Id, Arg.Any<CancellationToken>()).Returns(asset);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _purchaseStoreMock.DidNotReceive().HasPurchasesForAsset(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _assetStoreMock.DidNotReceive().Delete(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _assetStoreMock.DidNotReceive().SoftDelete(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceiveWithAnyArgs().Enqueue(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTransactionThrows_ShouldReturnError()
    {
        var authorId = Guid.NewGuid();
        var command = new DeleteAssetCommand(Guid.NewGuid(), authorId);
        var asset = new Asset { Id = command.Id, AuthorId = authorId, StorageKey = "key", CategoryId = Guid.NewGuid(), Title = "t", FileName = "f" };
        _assetStoreMock.GetById(command.Id).Returns(asset);
        _assetStoreMock.GetForUpdate(command.Id, Arg.Any<CancellationToken>()).Returns(asset);
        _assetStoreMock.Delete(command.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Error);
    }
}
