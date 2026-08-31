using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.UpdateAsset;
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

public class UpdateAssetCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ICategoryStore _categoryStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly UpdateAssetCommandHandler _handler;

    public UpdateAssetCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UpdateAssetCommandHandler(
            _assetStoreMock,
            _categoryStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<UpdateAssetCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "New Title", null, null, null);
        _assetStoreMock.GetById(command.AssetId).Returns((Asset?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbidden()
    {
        var command = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "New Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t" };
        _assetStoreMock.GetById(command.AssetId).Returns(asset);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_UPDATE
                && e.Outcome == AuditOutcome.DENIED
                && e.ResourceId == command.AssetId.ToString()),
            Arg.Any<CancellationToken>());
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCategoryIdProvidedAndNotFound_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, null, null, null, categoryId);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _categoryStoreMock.GetById(categoryId).Returns((Category?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithPartialUpdate_ShouldUpdateAndClearCache()
    {
        var authorId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Updated Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _assetStoreMock.Update(command.AssetId, "Updated Title", null, null, null, Arg.Any<CancellationToken>()).Returns(true);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).Update(command.AssetId, "Updated Title", null, null, null, Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_UPDATE
                && e.Outcome == AuditOutcome.SUCCESS
                && e.ResourceId == command.AssetId.ToString()
                && e.Metadata != null),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUpdateReturnsFalse_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _assetStoreMock.Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenExceptionThrown_ShouldLogSafeContextAndRethrow()
    {
        var testLogger = new TestLogger<UpdateAssetCommandHandler>();
        ICategoryStore categoryStoreMock = Substitute.For<ICategoryStore>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        IAuditWriter auditWriterMock = Substitute.For<IAuditWriter>();
        ICacheService cacheMock = Substitute.For<ICacheService>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new UpdateAssetCommandHandler(
            _assetStoreMock,
            categoryStoreMock,
            unitOfWorkMock,
            auditWriterMock,
            cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _assetStoreMock.Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB failed"));

        Func<Task<Result>> act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("DB failed");
        testLogger.Logs.Should().Contain(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && l.Message.Contains(command.AssetId.ToString())
            && l.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Handle_WhenAssetLookupThrows_ShouldLogSafeContextAndRethrow()
    {
        var testLogger = new TestLogger<UpdateAssetCommandHandler>();
        ICategoryStore categoryStoreMock = Substitute.For<ICategoryStore>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        IAuditWriter auditWriterMock = Substitute.For<IAuditWriter>();
        ICacheService cacheMock = Substitute.For<ICacheService>();
        var handler = new UpdateAssetCommandHandler(
            _assetStoreMock,
            categoryStoreMock,
            unitOfWorkMock,
            auditWriterMock,
            cacheMock,
            testLogger);

        var command = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "Title", null, null, null);
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB lookup failed"));

        Func<Task<Result>> act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("DB lookup failed");
        testLogger.Logs.Should().ContainSingle(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && l.Message.Contains(command.AssetId.ToString())
            && l.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Handle_WhenCategoryLookupThrows_ShouldLogSafeContextAndRethrow()
    {
        var testLogger = new TestLogger<UpdateAssetCommandHandler>();
        ICategoryStore categoryStoreMock = Substitute.For<ICategoryStore>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        IAuditWriter auditWriterMock = Substitute.For<IAuditWriter>();
        ICacheService cacheMock = Substitute.For<ICacheService>();
        var handler = new UpdateAssetCommandHandler(
            _assetStoreMock,
            categoryStoreMock,
            unitOfWorkMock,
            auditWriterMock,
            cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Title", null, null, categoryId);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>()).Returns(asset);
        categoryStoreMock.GetById(categoryId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Category lookup failed"));

        Func<Task<Result>> act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Category lookup failed");
        testLogger.Logs.Should().ContainSingle(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && l.Message.Contains(command.AssetId.ToString())
            && l.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Handle_WhenAssetLookupCancelled_ShouldRethrowWithoutErrorLogging()
    {
        var testLogger = new TestLogger<UpdateAssetCommandHandler>();
        ICategoryStore categoryStoreMock = Substitute.For<ICategoryStore>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        IAuditWriter auditWriterMock = Substitute.For<IAuditWriter>();
        ICacheService cacheMock = Substitute.For<ICacheService>();
        var handler = new UpdateAssetCommandHandler(
            _assetStoreMock,
            categoryStoreMock,
            unitOfWorkMock,
            auditWriterMock,
            cacheMock,
            testLogger);

        var command = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "Title", null, null, null);
        _assetStoreMock.GetById(command.AssetId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        Func<Task<Result>> act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        testLogger.Logs.Should().NotContain(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Handle_WhenCancelled_ShouldRethrowWithoutErrorLogging()
    {
        var testLogger = new TestLogger<UpdateAssetCommandHandler>();
        ICategoryStore categoryStoreMock = Substitute.For<ICategoryStore>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        IAuditWriter auditWriterMock = Substitute.For<IAuditWriter>();
        ICacheService cacheMock = Substitute.For<ICacheService>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new UpdateAssetCommandHandler(
            _assetStoreMock,
            categoryStoreMock,
            unitOfWorkMock,
            auditWriterMock,
            cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _assetStoreMock.Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        Func<Task<Result>> act = () => handler.Handle(command, CancellationToken.None);

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
