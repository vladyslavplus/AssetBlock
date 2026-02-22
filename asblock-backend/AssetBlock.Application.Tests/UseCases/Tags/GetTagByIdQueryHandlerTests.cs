using AssetBlock.Application.UseCases.Tags.GetTagById;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Tags;

public class GetTagByIdQueryHandlerTests
{
    private readonly ITagStore _tagStoreMock;
    private readonly GetTagByIdQueryHandler _handler;

    public GetTagByIdQueryHandlerTests()
    {
        _tagStoreMock = Substitute.For<ITagStore>();
        _handler = new GetTagByIdQueryHandler(_tagStoreMock);
    }

    [Fact]
    public async Task Handle_WhenFound_ShouldReturnDto()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var query = new GetTagByIdQuery(tagId);

        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns(new Tag { Id = tagId, Name = "my-tag" });

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert: Return the correct DTO mapped from entity
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("my-tag");
        result.Value.Id.Should().Be(tagId);
    }

    [Fact]
    public async Task Handle_WhenNotFound_ShouldReturnError()
    {
        // Arrange
        var tagId = Guid.NewGuid();
        var query = new GetTagByIdQuery(tagId);

        _tagStoreMock.GetById(tagId, Arg.Any<CancellationToken>()).Returns((Tag?)null);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert: Entity mismatch validation error
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_TAG_NOT_FOUND);
    }
}
