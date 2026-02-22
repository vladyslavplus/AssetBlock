using AssetBlock.Application.UseCases.Tags.DeleteTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class DeleteTagCommandHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteTagCommandHandler _handler;

    public DeleteTagCommandHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _cacheMock = Substitute.For<ICacheService>();
        _handler = new DeleteTagCommandHandler(_tagStoreMock, _cacheMock, NullLogger<DeleteTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenFound_ShouldDeleteAndClearCaches()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var command = new DeleteTagCommand(tagId);

        var existingTag = new Tag { Id = tagId, Name = "to-delete" };
        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns(existingTag);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert: Must delete entity and invalidate related caches
        result.IsSuccess.Should().BeTrue();
        await _tagStoreMock.Received(1).Delete(existingTag, Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var command = new DeleteTagCommand(tagId);

        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns((Tag?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _tagStoreMock.DidNotReceive().Delete(Arg.Any<Tag>(), Arg.Any<CancellationToken>());
    }
}
