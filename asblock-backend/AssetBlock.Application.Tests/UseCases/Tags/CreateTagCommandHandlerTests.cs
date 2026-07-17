using Ardalis.Result;
using AssetBlock.Application.UseCases.Tags.CreateTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class CreateTagCommandHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly CreateTagCommandHandler _handler;

    public CreateTagCommandHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new CreateTagCommandHandler(_tagStoreMock, _unitOfWorkMock, _auditWriterMock, _cacheMock, NullLogger<CreateTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenNameIsUnique_ShouldAddTagWriteAuditAndClearCache()
    {
        var command = new CreateTagCommand("new-tag");
        _tagStoreMock.GetByName("new-tag", Arg.Any<CancellationToken>()).Returns((Tag?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("new-tag");
        await _tagStoreMock.Received(1).Add(Arg.Is<Tag>(t => t.Name == "new-tag"), Arg.Any<CancellationToken>());
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.TAG_CREATE &&
                e.Outcome == AuditOutcome.SUCCESS),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNameExists_ShouldReturnError()
    {
        var command = new CreateTagCommand("existing-tag");
        _tagStoreMock.GetByName("existing-tag", Arg.Any<CancellationToken>()).Returns(new Tag { Id = Guid.NewGuid(), Name = "existing-tag" });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenAddThrowsDuplicateTagNameException_ShouldReturnError()
    {
        var command = new CreateTagCommand("race-tag");
        _tagStoreMock.GetByName("race-tag", Arg.Any<CancellationToken>()).Returns((Tag?)null);
        _tagStoreMock.Add(Arg.Any<Tag>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateTagNameException());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_ALREADY_EXISTS);
        await _cacheMock.DidNotReceive().RemoveByPrefix(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
