using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.RemoveAssetTag;
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

public class RemoveAssetTagCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ITagStore _tagStoreMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly RemoveAssetTagCommandHandler _handler;

    public RemoveAssetTagCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new RemoveAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<RemoveAssetTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _assetStoreMock.GetById(command.AssetId).Returns((Asset?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbidden()
    {
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t" };
        _assetStoreMock.GetById(command.AssetId).Returns(asset);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_TAG_REMOVE
                && e.Outcome == AuditOutcome.DENIED
                && e.ResourceId == command.AssetId.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagNotFound_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), authorId, Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetById(command.TagId).Returns((Tag?)null);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenTagNotOnAsset_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), authorId, Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
        var tag = new Tag { Id = command.TagId, Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetById(command.TagId).Returns(tag);
        _assetStoreMock.HasAssetTag(command.AssetId, command.TagId).Returns(false);

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_TAG_NOT_FOUND);
        await _assetStoreMock.DidNotReceive().RemoveTag(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_WhenSuccess_ShouldRemoveAndClearCache()
    {
        var authorId = Guid.NewGuid();
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), authorId, Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
        var tag = new Tag { Id = command.TagId, Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetById(command.TagId).Returns(tag);
        _assetStoreMock.HasAssetTag(command.AssetId, command.TagId).Returns(true);
        _assetStoreMock.RemoveTag(command.AssetId, command.TagId).Returns(Task.FromResult(true));

        Result result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _assetStoreMock.Received(1).RemoveTag(command.AssetId, command.TagId);
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_TAG_REMOVE
                && e.Outcome == AuditOutcome.SUCCESS
                && e.ResourceId == command.AssetId.ToString()
                && e.Metadata != null
                && e.Metadata.ContainsKey("tagId")),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX);
    }

    [Fact]
    public async Task Handle_WhenExceptionThrown_ShouldLogSafeContextAndRethrow()
    {
        var testLogger = new TestLogger<RemoveAssetTagCommandHandler>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new RemoveAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), authorId, Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
        var tag = new Tag { Id = command.TagId, Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetById(command.TagId).Returns(tag);
        _assetStoreMock.HasAssetTag(command.AssetId, command.TagId).Returns(true);
        _assetStoreMock.RemoveTag(command.AssetId, command.TagId).ThrowsAsync(new InvalidOperationException("db error"));

        Func<Task<Result>> act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("db error");
        testLogger.Logs.Should().Contain(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && l.Message.Contains(command.AssetId.ToString())
            && l.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Handle_WhenCancelled_ShouldRethrowWithoutErrorLogging()
    {
        var testLogger = new TestLogger<RemoveAssetTagCommandHandler>();
        IUnitOfWork unitOfWorkMock = Substitute.For<IUnitOfWork>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new RemoveAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), authorId, Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t" };
        var tag = new Tag { Id = command.TagId, Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetById(command.TagId).Returns(tag);
        _assetStoreMock.HasAssetTag(command.AssetId, command.TagId).Returns(true);
        _assetStoreMock.RemoveTag(command.AssetId, command.TagId).ThrowsAsync(new OperationCanceledException());

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
