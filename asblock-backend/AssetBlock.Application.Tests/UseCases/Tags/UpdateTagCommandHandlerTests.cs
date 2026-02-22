using AssetBlock.Application.UseCases.Tags.UpdateTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class UpdateTagCommandHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly UpdateTagCommandHandler _handler;

    public UpdateTagCommandHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _cacheMock = Substitute.For<ICacheService>();
        _handler = new UpdateTagCommandHandler(_tagStoreMock, _cacheMock, NullLogger<UpdateTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenFoundAndUnique_ShouldUpdateAndClearCache()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var command = new UpdateTagCommand(tagId, "updated-name");

        var existingTag = new Tag { Id = tagId, Name = "old-name" };
        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns(existingTag);
        _tagStoreMock.GetByName("updated-name", Arg.Any<CancellationToken>()).Returns((Tag?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert: Tag properties are updated and cache is cleared
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("updated-name");

        await _tagStoreMock.Received(1).Update(existingTag, Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
        existingTag.Name.Should().Be("updated-name");
    }

    [Fact]
    public async Task Handle_WhenNotFound_ShouldReturnError()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var command = new UpdateTagCommand(tagId, "updated-name");

        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns((Tag?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert: Tag doesn't exist, return error
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_TAG_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenNameAlreadyExists_ShouldReturnError()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var command = new UpdateTagCommand(tagId, "updated-name");

        var existingTag = new Tag { Id = tagId, Name = "old-name" };
        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns(existingTag);
        _tagStoreMock.GetByName("updated-name", Arg.Any<CancellationToken>()).Returns(new Tag { Id = Guid.NewGuid(), Name = "updated-name" });

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert: New name conflicts with existing tag
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_TAG_ALREADY_EXISTS);
    }
}
