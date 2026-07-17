using Ardalis.Result;
using AssetBlock.Application.UseCases.Tags.UpdateTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class UpdateTagCommandHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly UpdateTagCommandHandler _handler;

    public UpdateTagCommandHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UpdateTagCommandHandler(_tagStoreMock, _unitOfWorkMock, _auditWriterMock, _cacheMock, NullLogger<UpdateTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenFoundAndUnique_ShouldUpdateWriteAuditAndClearCache()
    {
        var tagId = Guid.NewGuid();
        var command = new UpdateTagCommand(tagId, "updated-name");

        var existingTag = new Tag { Id = tagId, Name = "old-name" };
        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns(existingTag);
        _tagStoreMock.GetByName("updated-name", Arg.Any<CancellationToken>()).Returns((Tag?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("updated-name");
        await _tagStoreMock.Received(1).Update(existingTag, Arg.Any<CancellationToken>());
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.TAG_UPDATE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == tagId.ToString()),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
        existingTag.Name.Should().Be("updated-name");
    }

    [Fact]
    public async Task Handle_WhenNotFound_ShouldReturnNotFound()
    {
        var tagId = Guid.NewGuid();
        var command = new UpdateTagCommand(tagId, "updated-name");
        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns((Tag?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenNameAlreadyExists_ShouldReturnError()
    {
        var tagId = Guid.NewGuid();
        var command = new UpdateTagCommand(tagId, "updated-name");

        var existingTag = new Tag { Id = tagId, Name = "old-name" };
        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns(existingTag);
        _tagStoreMock.GetByName("updated-name", Arg.Any<CancellationToken>()).Returns(new Tag { Id = Guid.NewGuid(), Name = "updated-name" });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_ALREADY_EXISTS);
    }
}
