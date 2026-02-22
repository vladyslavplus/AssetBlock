using AssetBlock.Application.UseCases.Tags.CreateTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class CreateTagCommandHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly CreateTagCommandHandler _handler;

    public CreateTagCommandHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _cacheMock = Substitute.For<ICacheService>();
        _handler = new CreateTagCommandHandler(_tagStoreMock, _cacheMock, NullLogger<CreateTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenNameIsUnique_ShouldAddTagAndClearCache()
    {
        // Arrange
        var command = new CreateTagCommand("new-tag");

        _tagStoreMock.GetByName("new-tag", Arg.Any<CancellationToken>()).Returns((Tag?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert: Tag is created and cache is cleared
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("new-tag");

        await _tagStoreMock.Received(1).Add(Arg.Is<Tag>(t => t.Name == "new-tag"), Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNameExists_ShouldReturnError()
    {
        // Arrange
        var command = new CreateTagCommand("existing-tag");

        _tagStoreMock.GetByName("existing-tag", Arg.Any<CancellationToken>()).Returns(new Tag { Id = Guid.NewGuid(), Name = "existing-tag" });

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert: Return validation error
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_TAG_ALREADY_EXISTS);
    }
}
