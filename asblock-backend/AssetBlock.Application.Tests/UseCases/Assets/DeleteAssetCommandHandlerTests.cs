using AssetBlock.Application.UseCases.Assets.DeleteAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class DeleteAssetCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly IAssetSearchService _searchServiceMock;
    private readonly IAssetStorageService _storageServiceMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteAssetCommandHandler _handler;

    public DeleteAssetCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _searchServiceMock = Substitute.For<IAssetSearchService>();
        _storageServiceMock = Substitute.For<IAssetStorageService>();
        _cacheMock = Substitute.For<ICacheService>();
        
        _handler = new DeleteAssetCommandHandler(
            _assetStoreMock,
            _searchServiceMock,
            _storageServiceMock,
            _cacheMock,
            NullLogger<DeleteAssetCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var command = new DeleteAssetCommand(Guid.NewGuid(), Guid.NewGuid());
        _assetStoreMock.GetById(command.Id).Returns((Asset?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbidden()
    {
        // Arrange
        var command = new DeleteAssetCommand(Guid.NewGuid(), Guid.NewGuid());
        var asset = new Asset { Id = command.Id, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.Id).Returns(asset);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
    }

    [Fact]
    public async Task Handle_WhenSuccess_ShouldDeleteAndRemoveCache()
    {
        // Arrange
        var authorId = Guid.NewGuid();
        var command = new DeleteAssetCommand(Guid.NewGuid(), authorId);
        var asset = new Asset { Id = command.Id, AuthorId = authorId, StorageKey = "key", CategoryId = Guid.NewGuid(), Title = "t", FileName = "f" };
        _assetStoreMock.GetById(command.Id).Returns(asset);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).Delete(command.Id);
        await _searchServiceMock.Received(1).DeleteAsset(command.Id);
        await _storageServiceMock.Received(1).Delete("key");
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX);
    }
}
