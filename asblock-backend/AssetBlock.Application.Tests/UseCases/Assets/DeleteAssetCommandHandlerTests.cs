using AssetBlock.Application.UseCases.Assets.DeleteAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class DeleteAssetCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly IPurchaseStore _purchaseStoreMock;
    private readonly IOutboxStore _outboxStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteAssetCommandHandler _handler;

    public DeleteAssetCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        var checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _outboxStoreMock = Substitute.For<IOutboxStore>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();
        _purchaseStoreMock.HasPurchasesForAsset(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        checkoutIntentStoreMock.HasActiveForAsset(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new DeleteAssetCommandHandler(
            _assetStoreMock,
            _purchaseStoreMock,
            checkoutIntentStoreMock,
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
        var asset = new Asset { Id = command.Id, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t" };
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
        var asset = new Asset { Id = command.Id, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
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
        var asset = new Asset { Id = command.Id, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
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
            CategoryId = Guid.NewGuid(),
            Title = "t",
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
    public async Task Handle_WhenActiveCheckoutExists_ShouldSoftDeleteWithAuditWithoutOutbox()
    {
        var authorId = Guid.NewGuid();
        var command = new DeleteAssetCommand(Guid.NewGuid(), authorId);
        var asset = new Asset { Id = command.Id, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
        _assetStoreMock.GetById(command.Id).Returns(asset);
        _assetStoreMock.GetForUpdate(command.Id, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.HasPurchasesForAsset(command.Id, Arg.Any<CancellationToken>()).Returns(false);

        // Active checkout present (purchases are absent)
        var checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
        checkoutIntentStoreMock.HasActiveForAsset(command.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new DeleteAssetCommandHandler(
            _assetStoreMock,
            _purchaseStoreMock,
            checkoutIntentStoreMock,
            unitOfWorkMock,
            _outboxStoreMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<DeleteAssetCommandHandler>.Instance);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).SoftDelete(command.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _assetStoreMock.DidNotReceive().Delete(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _outboxStoreMock.DidNotReceiveWithAnyArgs().Enqueue(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_SOFT_DELETE &&
                e.Outcome == AuditOutcome.SUCCESS),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTransactionThrows_ShouldLogSafeContextAndRethrow()
    {
        var testLogger = new TestLogger<DeleteAssetCommandHandler>();
        var checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new DeleteAssetCommandHandler(
            _assetStoreMock,
            _purchaseStoreMock,
            checkoutIntentStoreMock,
            unitOfWorkMock,
            _outboxStoreMock,
            _auditWriterMock,
            _cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var command = new DeleteAssetCommand(Guid.NewGuid(), authorId);
        var asset = new Asset { Id = command.Id, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
        _assetStoreMock.GetById(command.Id).Returns(asset);
        _assetStoreMock.GetForUpdate(command.Id, Arg.Any<CancellationToken>()).Returns(asset);
        _assetStoreMock.Delete(command.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db"));

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("db");
        testLogger.Logs.Should().Contain(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && l.Message.Contains(command.Id.ToString())
            && l.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Handle_WhenCancelled_ShouldRethrowWithoutErrorLogging()
    {
        var testLogger = new TestLogger<DeleteAssetCommandHandler>();
        var checkoutIntentStoreMock = Substitute.For<ICheckoutIntentStore>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new DeleteAssetCommandHandler(
            _assetStoreMock,
            _purchaseStoreMock,
            checkoutIntentStoreMock,
            unitOfWorkMock,
            _outboxStoreMock,
            _auditWriterMock,
            _cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var command = new DeleteAssetCommand(Guid.NewGuid(), authorId);
        var asset = new Asset { Id = command.Id, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
        _assetStoreMock.GetById(command.Id).Returns(asset);
        _assetStoreMock.GetForUpdate(command.Id, Arg.Any<CancellationToken>()).Returns(asset);
        _assetStoreMock.Delete(command.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        testLogger.Logs.Should().NotContain(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
