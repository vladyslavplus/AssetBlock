using AssetBlock.Application.UseCases.Assets.Events;
using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class UpdateAssetCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ICategoryStore _categoryStoreMock;
    private readonly IPublisher _publisherMock;
    private readonly ICacheService _cacheMock;
    private readonly UpdateAssetCommandHandler _handler;

    public UpdateAssetCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _publisherMock = Substitute.For<IPublisher>();
        _cacheMock = Substitute.For<ICacheService>();

        _handler = new UpdateAssetCommandHandler(
            _assetStoreMock,
            _categoryStoreMock,
            _publisherMock,
            _cacheMock,
            NullLogger<UpdateAssetCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var command = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "New Title", null, null, null);
        _assetStoreMock.GetById(command.AssetId).Returns((Asset?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbidden()
    {
        // Arrange
        var command = new UpdateAssetCommand(Guid.NewGuid(), Guid.NewGuid(), "New Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };
        _assetStoreMock.GetById(command.AssetId).Returns(asset);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCategoryIdProvidedAndNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, null, null, null, categoryId);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _categoryStoreMock.GetById(categoryId).Returns((Category?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        await _assetStoreMock.DidNotReceive().Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithPartialUpdate_ShouldUpdateOnlyProvidedFieldsAndReindex()
    {
        // Arrange
        var authorId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Updated Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _assetStoreMock.Update(command.AssetId, "Updated Title", null, null, null, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).Update(command.AssetId, "Updated Title", null, null, null, Arg.Any<CancellationToken>());
        await _publisherMock.Received(1).Publish(Arg.Is<AssetCreatedEvent>(e => e.AssetId == command.AssetId));
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenUpdateReturnsFalse_ShouldReturnNotFound()
    {
        // Arrange
        var authorId = Guid.NewGuid();
        var command = new UpdateAssetCommand(Guid.NewGuid(), authorId, "Title", null, null, null);
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _assetStoreMock.Update(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }
}
