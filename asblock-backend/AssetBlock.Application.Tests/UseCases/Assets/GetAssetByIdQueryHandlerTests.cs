using AssetBlock.Application.UseCases.Assets.GetAssetById;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class GetAssetByIdQueryHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly GetAssetByIdQueryHandler _handler;

    public GetAssetByIdQueryHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _handler = new GetAssetByIdQueryHandler(_assetStoreMock);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnError()
    {
        // Arrange
        var query = new GetAssetByIdQuery(Guid.NewGuid());
        _assetStoreMock.GetById(query.Id, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenAssetFound_ShouldReturnMappedDetailItem()
    {
        // Arrange
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var category = new Category { Id = categoryId, Name = "Audio", Slug = "audio" };
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "Beat Pack vol. 1",
            Description = "A great pack",
            Price = 14.99m,
            StorageKey = "assets/auth/beat.zip",
            FileName = "beat.zip",
            CreatedAt = now,
            Category = category
        };

        var query = new GetAssetByIdQuery(assetId);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(asset);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(assetId);
        result.Value.Title.Should().Be("Beat Pack vol. 1");
        result.Value.Price.Should().Be(14.99m);
        result.Value.CategoryId.Should().Be(categoryId);
        result.Value.CategoryName.Should().Be("Audio");
        result.Value.AuthorId.Should().Be(authorId);
        result.Value.CreatedAt.Should().Be(now);
    }
}
