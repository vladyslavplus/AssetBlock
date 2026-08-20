using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetAssetById;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
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
        var reviewStoreMock = Substitute.For<IReviewStore>();
        reviewStoreMock.GetAverageRatingForAsset(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(0.0);
        _handler = new GetAssetByIdQueryHandler(_assetStoreMock, reviewStoreMock);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var query = new GetAssetByIdQuery(Guid.NewGuid());
        _assetStoreMock.GetById(query.Id, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenDelisted_ShouldReturnNotFound()
    {
        var assetId = Guid.NewGuid();
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            Title = "Gone",
            DeletedAt = DateTimeOffset.UtcNow,
        };
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(asset);

        var result = await _handler.Handle(new GetAssetByIdQuery(assetId), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
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
        var author = new User
        {
            Id = authorId,
            Username = "beatmaker",
            Email = "author@test.local",
            PasswordHash = "hash",
            Role = AppRoles.USER
        };
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "Beat Pack vol. 1",
            Description = "A great pack",
            Price = 14.99m,
            CreatedAt = now,
            Category = category,
            Author = author
        };

        var versionId = Guid.NewGuid();
        var snapshot = new AssetCurrentVersionSnapshot(
            AssetId: assetId,
            AssetVersionId: versionId,
            AuthorId: authorId,
            Title: "Beat Pack vol. 1",
            Description: "A great pack",
            Price: 14.99m,
            DeletedAt: null,
            VersionNumber: 1,
            VersionCreatedAt: now,
            FileName: "beat.zip",
            StorageKey: "assets/auth/beat.zip",
            ContentLength: 1024,
            ContentSha256: new string('a', 64),
            LicenseCode: "PERSONAL",
            LicenseTemplateVersion: "1.0",
            LicenseDisplayName: "Personal use",
            LicenseTerms: "terms");

        var query = new GetAssetByIdQuery(assetId);
        _assetStoreMock.GetById(assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _assetStoreMock.GetCurrentVersionSnapshot(assetId, Arg.Any<CancellationToken>()).Returns(snapshot);

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
        result.Value.AuthorUsername.Should().Be("beatmaker");
        result.Value.Description.Should().Be("A great pack");
        result.Value.CreatedAt.Should().Be(now);
        result.Value.UpdatedAt.Should().BeNull();
        result.Value.Tags.Should().BeEmpty();
        result.Value.AverageRating.Should().Be(0);
        result.Value.CurrentVersionNumber.Should().Be(1);
        result.Value.CurrentLicense.Code.Should().Be("PERSONAL");
    }
}
