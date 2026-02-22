using AssetBlock.Application.UseCases.Assets.AddAssetTag;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class AddAssetTagCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly ITagStore _tagStoreMock;
    private readonly AddAssetTagCommandHandler _handler;

    public AddAssetTagCommandHandlerTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        var publisherMock = Substitute.For<IPublisher>();
        var cacheMock = Substitute.For<ICacheService>();

        _handler = new AddAssetTagCommandHandler(
            _assetStoreMock,
            _tagStoreMock,
            publisherMock,
            cacheMock,
            NullLogger<AddAssetTagCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenAssetNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var command = new AddAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), "test");
        _assetStoreMock.GetById(command.AssetId).Returns((Asset?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenUserIsNotAuthor_ShouldReturnForbidden()
    {
        // Arrange
        var command = new AddAssetTagCommand(Guid.NewGuid(), Guid.NewGuid(), "test");
        var asset = new Asset { Id = command.AssetId, AuthorId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f", AssetTags = [] };
        _assetStoreMock.GetById(command.AssetId).Returns(asset);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
    }

    [Fact]
    public async Task Handle_WhenTagDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, " New-Tag ");
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f", AssetTags = [] };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetByName("new-tag").Returns((Tag?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _tagStoreMock.DidNotReceive().Add(Arg.Any<Tag>());
        await _assetStoreMock.DidNotReceive().AddTag(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_WhenTagAlreadyOnAsset_ShouldReturnConflict()
    {
        // Arrange
        var authorId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = Guid.NewGuid(),
            Title = "t",
            StorageKey = "k",
            FileName = "f",
            AssetTags = [new AssetTag { AssetId = assetId, TagId = tag.Id }]
        };
        var command = new AddAssetTagCommand(assetId, authorId, "existing");

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetByName("existing").Returns(tag);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_TAG_ALREADY_EXISTS);
        await _assetStoreMock.DidNotReceive().AddTag(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_WhenTagExists_ShouldJustAddTagLink()
    {
        // Arrange
        var authorId = Guid.NewGuid();
        var command = new AddAssetTagCommand(Guid.NewGuid(), authorId, "existing");
        var asset = new Asset { Id = command.AssetId, AuthorId = authorId, CategoryId = Guid.NewGuid(), Title = "t", StorageKey = "k", FileName = "f", AssetTags = [] };
        var tag = new Tag { Id = Guid.NewGuid(), Name = "existing" };

        _assetStoreMock.GetById(command.AssetId).Returns(asset);
        _tagStoreMock.GetByName("existing").Returns(tag);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        await _tagStoreMock.DidNotReceive().Add(Arg.Any<Tag>());
        await _assetStoreMock.Received(1).AddTag(command.AssetId, tag.Id);
    }
}
