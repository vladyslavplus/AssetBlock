using AssetBlock.Application.UseCases.Assets.RemoveAssetTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class RemoveAssetTagCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ITagStore _tagStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly RemoveAssetTagCommandHandler _handler;

    public RemoveAssetTagCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        _cacheMock = Substitute.For<ICacheService>();

        _handler = new RemoveAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            _cacheMock,
            NullLogger<RemoveAssetTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _assetStoreMock.GetById(command.AssetId).Returns((Asset?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbidden()
    {
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId).Returns(asset);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
    }

    [Fact]
    public async Task Handle_WhenTagNotFound_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), authorId, Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetById(command.TagId).Returns((Tag?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenTagNotOnAsset_ShouldReturnNotFound()
    {
        var authorId = Guid.NewGuid();
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), authorId, Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };
        var tag = new Tag { Id = command.TagId, Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetById(command.TagId).Returns(tag);
        _assetStoreMock.HasAssetTag(command.AssetId, command.TagId).Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_TAG_NOT_FOUND);
        await _assetStoreMock.DidNotReceive().RemoveTag(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_WhenSuccess_ShouldRemoveAndClearCache()
    {
        var authorId = Guid.NewGuid();
        var command = new RemoveAssetTagCommand(Guid.NewGuid(), authorId, Guid.NewGuid());
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };
        var tag = new Tag { Id = command.TagId, Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetById(command.TagId).Returns(tag);
        _assetStoreMock.HasAssetTag(command.AssetId, command.TagId).Returns(true);
        _assetStoreMock.RemoveTag(command.AssetId, command.TagId).Returns(Task.FromResult(true));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _assetStoreMock.Received(1).RemoveTag(command.AssetId, command.TagId);
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX);
    }
}
