using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.AddAssetTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class AddAssetTagCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ITagStore _tagStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly AddAssetTagCommandHandler _handler;

    public AddAssetTagCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        ICacheService cacheMock = Substitute.For<ICacheService>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new AddAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            _unitOfWorkMock,
            _auditWriterMock,
            cacheMock,
            NullLogger<AddAssetTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new AddAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), "test");
        _assetStoreMock.GetOwnership(command.AssetId).Returns((AssetOwnershipDto?)null);

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbiddenAndWriteDeniedAudit()
    {
        var command = new AddAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), "test");
        var ownership = new AssetOwnershipDto(command.AssetId, Guid.NewGuid(), false);
        _assetStoreMock.GetOwnership(command.AssetId).Returns(ownership);

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
        await _auditWriterMock.Received(1).WriteBestEffort(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_TAG_ADD &&
                e.Outcome == AuditOutcome.DENIED &&
                e.ResourceId == ownership.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagDoesNotExist_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, " New-Tag ");
        var ownership = new AssetOwnershipDto(command.AssetId, authorId, false);

        _assetStoreMock.GetOwnership(command.AssetId).Returns(ownership);
        _tagStoreMock.GetByName("new-tag").Returns((Tag?)null);

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _tagStoreMock.DidNotReceive().Add(Arg.Any<Tag>());
        await _assetStoreMock.DidNotReceive().TryAddTag(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_WhenTagAlreadyOnAsset_ShouldReturnConflict()
    {
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };
        var ownership = new AssetOwnershipDto(assetId, authorId, false);
        var command = new AddAssetTagCommand(assetId, authorId, "existing");

        _assetStoreMock.GetOwnership(command.AssetId).Returns(ownership);
        _tagStoreMock.GetByName("existing").Returns(tag);
        _assetStoreMock.TryAddTag(assetId, tag.Id, Arg.Any<CancellationToken>()).Returns(false);

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_TAG_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenTagExists_ShouldAddTagLinkAndWriteAuditInsideTransaction()
    {
        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, "existing");
        var ownership = new AssetOwnershipDto(command.AssetId, authorId, false);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };

        _assetStoreMock.GetOwnership(command.AssetId).Returns(ownership);
        _tagStoreMock.GetByName("existing").Returns(tag);
        _assetStoreMock.TryAddTag(command.AssetId, tag.Id, Arg.Any<CancellationToken>()).Returns(true);

        Result<TagDto> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("existing");
        await _tagStoreMock.DidNotReceive().Add(Arg.Any<Tag>());
        await _assetStoreMock.Received(1).TryAddTag(command.AssetId, tag.Id, Arg.Any<CancellationToken>());
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_TAG_ADD &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.Metadata != null && e.Metadata.ContainsKey("tagId")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAddTagThrows_ShouldLogSafeContextAndRethrow()
    {
        var testLogger = new TestLogger<AddAssetTagCommandHandler>();
        ICacheService cacheMock = Substitute.For<ICacheService>();
        var handler = new AddAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            _unitOfWorkMock,
            _auditWriterMock,
            cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, "existing");
        var ownership = new AssetOwnershipDto(command.AssetId, authorId, false);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };

        _assetStoreMock.GetOwnership(command.AssetId).Returns(ownership);
        _tagStoreMock.GetByName("existing").Returns(tag);
        _assetStoreMock.TryAddTag(command.AssetId, tag.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db"));

        Func<Task<Result<TagDto>>> act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("db");
        testLogger.Logs.Should().Contain(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && l.Message.Contains(command.AssetId.ToString())
            && l.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Handle_WhenCancelled_ShouldRethrowWithoutErrorLogging()
    {
        var testLogger = new TestLogger<AddAssetTagCommandHandler>();
        ICacheService cacheMock = Substitute.For<ICacheService>();
        var handler = new AddAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            _unitOfWorkMock,
            _auditWriterMock,
            cacheMock,
            testLogger);

        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, "existing");
        var ownership = new AssetOwnershipDto(command.AssetId, authorId, false);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };

        _assetStoreMock.GetOwnership(command.AssetId).Returns(ownership);
        _tagStoreMock.GetByName("existing").Returns(tag);
        _assetStoreMock.TryAddTag(command.AssetId, tag.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        Func<Task<Result<TagDto>>> act = () => handler.Handle(command, CancellationToken.None);

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
