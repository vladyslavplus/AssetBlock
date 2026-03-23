using AssetBlock.Application.UseCases.Assets.Events;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Assets.Events;

public class AssetIndexEventHandlerTests
{
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly IAssetSearchService _search = Substitute.For<IAssetSearchService>();
    private readonly AssetIndexEventHandler _handler;

    public AssetIndexEventHandlerTests()
    {
        _handler = new AssetIndexEventHandler(_assetStore, _search, NullLogger<AssetIndexEventHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetMissing_ShouldNotIndex()
    {
        var assetId = Guid.NewGuid();
        _assetStore.GetById(assetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        await _handler.Handle(new AssetCreatedEvent(assetId), CancellationToken.None);

        await _search.DidNotReceive().IndexAsset(Arg.Any<AssetDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAssetFound_ShouldIndexWithTagsAndMetadata()
    {
        var assetId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "Pack",
            Description = "Desc",
            Price = 9.99m,
            StorageKey = "key",
            FileName = "f.zip",
            CreatedAt = DateTimeOffset.UtcNow,
            Author = new User
            {
                Id = authorId,
                Username = "author",
                Email = "a@a.com",
                PasswordHash = "h",
                Role = "User"
            },
            Category = new Category
            {
                Id = categoryId,
                Name = "Cat",
                Slug = "cat"
            },
            AssetTags =
            [
                new AssetTag
                {
                    AssetId = assetId,
                    TagId = tagId,
                    Tag = new Tag { Id = tagId, Name = "  Rust  " }
                }
            ]
        };
        _assetStore.GetById(assetId, Arg.Any<CancellationToken>()).Returns(asset);

        await _handler.Handle(new AssetCreatedEvent(assetId), CancellationToken.None);

        await _search.Received(1).IndexAsset(
            Arg.Is<AssetDocument>(d =>
                d.Id == assetId
                && d.Title == "Pack"
                && d.Description == "Desc"
                && d.CategorySlug == "cat"
                && d.AuthorUsername == "author"
                && d.Tags.Count == 1
                && d.Tags[0] == "rust"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenIndexThrows_ShouldSwallowAndLog()
    {
        var assetId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "T",
            Description = null,
            Price = 1,
            StorageKey = "k",
            FileName = "f.zip",
            CreatedAt = DateTimeOffset.UtcNow,
            Author = new User
            {
                Id = authorId,
                Username = "u",
                Email = "e@e.com",
                PasswordHash = "h",
                Role = "User"
            },
            Category = new Category { Id = categoryId, Name = "C", Slug = "c" },
            AssetTags = []
        };
        _assetStore.GetById(assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _search.IndexAsset(Arg.Any<AssetDocument>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("es down"));

        var act = async () => await _handler.Handle(new AssetCreatedEvent(assetId), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
