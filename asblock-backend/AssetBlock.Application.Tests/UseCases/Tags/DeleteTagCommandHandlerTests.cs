using AssetBlock.Application.UseCases.Tags.DeleteTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class DeleteTagCommandHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteTagCommandHandler _handler;

    public DeleteTagCommandHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new DeleteTagCommandHandler(_tagStoreMock, _unitOfWorkMock, _auditWriterMock, _cacheMock, NullLogger<DeleteTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenFound_ShouldDeleteWriteAuditAndClearCaches()
    {
        var tagId = Guid.NewGuid();
        var command = new DeleteTagCommand(tagId);
        var existingTag = new Tag { Id = tagId, Name = "to-delete" };
        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns(existingTag);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _tagStoreMock.Received(1).Delete(existingTag, Arg.Any<CancellationToken>());
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.TAG_DELETE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == tagId.ToString()),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNotFound_ShouldReturnNotFound()
    {
        var tagId = Guid.NewGuid();
        var command = new DeleteTagCommand(tagId);
        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns((Tag?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _tagStoreMock.DidNotReceive().Delete(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }
}
