using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.UploadAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class UploadAssetCommandHandlerTests
{
    private readonly ICategoryStore _categoryStoreMock;
    private readonly IAssetStore _assetStoreMock;
    private readonly ITagStore _tagStoreMock;
    private readonly IAssetStorageService _assetStorageServiceMock;
    private readonly IEncryptionService _encryptionServiceMock;
    private readonly IOutboxStore _outboxStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly UploadAssetCommandHandler _handler;

    public UploadAssetCommandHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        _assetStorageServiceMock = Substitute.For<IAssetStorageService>();
        _encryptionServiceMock = Substitute.For<IEncryptionService>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _outboxStoreMock = Substitute.For<IOutboxStore>();
        _cacheMock = Substitute.For<ICacheService>();

        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UploadAssetCommandHandler(
            _categoryStoreMock,
            _assetStoreMock,
            _tagStoreMock,
            _assetStorageServiceMock,
            _encryptionServiceMock,
            unitOfWorkMock,
            _outboxStoreMock,
            _cacheMock,
            NullLogger<UploadAssetCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenCategoryNotFound_ShouldReturnNotFound()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid());
        var command = new UploadAssetCommand(Guid.NewGuid(), request, new MemoryStream(), "test.png");

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns((Category?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenEncryptionFails_ShouldReturnError()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid());
        var command = new UploadAssetCommand(Guid.NewGuid(), request, new MemoryStream(), "test.png");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _encryptionServiceMock.Encrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Encryption Error"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
    }

    [Fact]
    public async Task Handle_WhenStorageUploadFails_ShouldReturnError()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid());
        var command = new UploadAssetCommand(Guid.NewGuid(), request, new MemoryStream(), "test.png");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _assetStorageServiceMock.Upload(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Storage Error"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
    }

    [Fact]
    public async Task Handle_WhenDbAddFails_ShouldAttemptToDeleteStorageAndThrow()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid());
        var command = new UploadAssetCommand(Guid.NewGuid(), request, new MemoryStream(), "test.png");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _assetStoreMock.Add(Arg.Any<Asset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB Error"));

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("DB Error");
        await _assetStorageServiceMock.Received(1).Delete(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnAssetIdClearCacheAndEnqueueIndex()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), 10);
        var command = new UploadAssetCommand(Guid.NewGuid(), request, new MemoryStream(), "test.png");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();

        await _assetStoreMock.Received(1).Add(Arg.Is<Asset>(a =>
            a.Id == result.Value &&
            a.Title == "Title" &&
            a.DownloadLimitPerHour == 10 &&
            a.StorageKey.EndsWith(".png")
        ), Arg.Any<CancellationToken>());

        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.ASSET_INDEX_UPSERT,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagsPresent_ShouldVerifyTagsAndCallAddWithTags()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), 10)
        {
            Tags = ["tag1", "  TAG2 "]
        };
        var command = new UploadAssetCommand(Guid.NewGuid(), request, new MemoryStream(), "test.png");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);

        var existingTags = new List<Tag>
        {
            new() { Id = Guid.NewGuid(), Name = "tag1" },
            new() { Id = Guid.NewGuid(), Name = "tag2" }
        };
        _tagStoreMock.GetTagsByNames(Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => existingTags);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _tagStoreMock.Received(1).GetTagsByNames(Arg.Is<List<string>>(list =>
            list.Count == 2 && list.Contains("tag1") && list.Contains("tag2")), Arg.Any<CancellationToken>());

        await _assetStoreMock.Received(1).AddWithTags(Arg.Any<Asset>(), existingTags, Arg.Any<CancellationToken>());
        await _outboxStoreMock.Received(1).Enqueue(
            OutboxMessageTypes.ASSET_INDEX_UPSERT,
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagsMissing_ShouldReturnError()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), 10)
        {
            Tags = ["tag1", "nonexistent"]
        };
        var command = new UploadAssetCommand(Guid.NewGuid(), request, new MemoryStream(), "test.png");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);

        var existingTags = new List<Tag>
        {
            new() { Id = Guid.NewGuid(), Name = "tag1" }
        };
        _tagStoreMock.GetTagsByNames(Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(existingTags);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _assetStoreMock.DidNotReceiveWithAnyArgs().AddWithTags(Arg.Any<Asset>(), Arg.Any<List<Tag>>(), Arg.Any<CancellationToken>());
    }
}
