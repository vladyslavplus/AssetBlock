using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.UploadAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
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
    private readonly IAssetArchiveInspector _archiveInspectorMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly UploadAssetCommandHandler _handler;

    public UploadAssetCommandHandlerTests()
    {
        _categoryStoreMock = Substitute.For<ICategoryStore>();
        _assetStoreMock = Substitute.For<IAssetStore>();
        _tagStoreMock = Substitute.For<ITagStore>();
        _assetStorageServiceMock = Substitute.For<IAssetStorageService>();
        _encryptionServiceMock = Substitute.For<IEncryptionService>();
        _archiveInspectorMock = Substitute.For<IAssetArchiveInspector>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        _encryptionServiceMock.ComputeCiphertextLength(Arg.Any<long>()).Returns(4L);
        _archiveInspectorMock.Inspect(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UploadAssetCommandHandler(
            _categoryStoreMock,
            _assetStoreMock,
            _tagStoreMock,
            _assetStorageServiceMock,
            _encryptionServiceMock,
            _archiveInspectorMock,
            Microsoft.Extensions.Options.Options.Create(new FileUploadOptions()),
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<UploadAssetCommandHandler>.Instance);
    }

    private static UploadAssetRequest DefaultRequest(string title = "Title", string desc = "Desc", decimal price = 100m, string licenseCode = "PERSONAL") =>
        new(title, desc, price, Guid.NewGuid(), licenseCode);

    private static UploadAssetCommand CreateCommand(UploadAssetRequest request, string fileName = "test.zip", long length = 1) =>
        new(Guid.NewGuid(), request, new MemoryStream([1]), fileName, length);

    [Fact]
    public async Task Handle_WhenFileTooLarge_ShouldReturnError()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request, length: 250L * 1024 * 1024 + 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_FILE_TOO_LARGE);
    }

    [Fact]
    public async Task Handle_WhenFileIsExactlyAtConfiguredLimit_ShouldAcceptUpload()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request, length: 250L * 1024 * 1024);
        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>())
            .Returns(new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _encryptionServiceMock.Received(1).ComputeCiphertextLength(250L * 1024 * 1024);
    }

    [Fact]
    public async Task Handle_WhenExtensionNotAllowed_ShouldReturnError()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request, fileName: "test.png");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_FILE_EXTENSION_NOT_ALLOWED);
    }

    [Fact]
    public async Task Handle_WhenArchiveRejected_ShouldReturnError()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };
        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _archiveInspectorMock.Inspect(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArchiveRejectedException("bad archive"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ARCHIVE_REJECTED);
    }

    [Fact]
    public async Task Handle_WhenCategoryNotFound_ShouldReturnNotFound()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request);

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns((Category?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenEncryptionFails_ShouldReturnError()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request);
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
        var request = DefaultRequest();
        var command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _assetStorageServiceMock.Upload(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Storage Error"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
    }

    [Fact]
    public async Task Handle_WhenStreamingUploadSucceeds_ShouldPipeCiphertextWithoutSeekableBuffer()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request);
        var ciphertext = "ciphertext"u8.ToArray();
        byte[]? uploaded = null;
        bool? uploadStreamCanSeek = null;
        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>())
            .Returns(new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" });
        _encryptionServiceMock.Encrypt(
                Arg.Any<Stream>(),
                Arg.Any<Stream>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var output = callInfo.ArgAt<Stream>(1);
                await output.WriteAsync(ciphertext);
            });
        _assetStorageServiceMock.Upload(
                Arg.Any<string>(),
                Arg.Any<Stream>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var input = callInfo.ArgAt<Stream>(1);
                uploadStreamCanSeek = input.CanSeek;
                await using var destination = new MemoryStream();
                await input.CopyToAsync(destination);
                uploaded = destination.ToArray();
            });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        uploadStreamCanSeek.Should().BeFalse();
        uploaded.Should().Equal(ciphertext);
    }

    [Fact]
    public async Task Handle_WhenStreamingIsCancelled_ShouldPropagateCancellation()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request);
        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>())
            .Returns(new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" });
        _encryptionServiceMock.Encrypt(
                Arg.Any<Stream>(),
                Arg.Any<Stream>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _handler.Handle(command, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _assetStoreMock.DidNotReceiveWithAnyArgs().AddWithVersion(
            Arg.Any<Asset>(),
            Arg.Any<AssetVersion>(),
            Arg.Any<List<Tag>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDbAddFails_ShouldAttemptToDeleteStorageAndThrow()
    {
        var request = DefaultRequest();
        var command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _assetStoreMock.AddWithVersion(Arg.Any<Asset>(), Arg.Any<AssetVersion>(), Arg.Any<List<Tag>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB Error"));

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("DB Error");
        await _assetStorageServiceMock.Received(1).Delete(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnAssetIdClearCache()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), "PERSONAL", 10);
        var command = CreateCommand(request, fileName: "path/to/MyArchive.TAR.GZ");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();

        await _assetStoreMock.Received(1).AddWithVersion(
            Arg.Is<Asset>(a =>
                a.Id == result.Value &&
                a.Title == "Title" &&
                a.DownloadLimitPerHour == 10),
            Arg.Is<AssetVersion>(v =>
                v.FileName == "MyArchive.TAR.GZ" &&
                v.StorageKey.Contains(result.Value.ToString()) &&
                v.StorageKey.EndsWith(".tar.gz")),
            Arg.Any<List<Tag>?>(),
            Arg.Any<CancellationToken>());

        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.ASSET_CREATE
                && e.Outcome == AuditOutcome.SUCCESS
                && e.ResourceId == result.Value.ToString()
                && e.Metadata != null
                && e.Metadata.ContainsKey("categoryId")
                && e.Metadata.ContainsKey("tagCount")),
            Arg.Any<CancellationToken>());

        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagsPresent_ShouldVerifyTagsAndCallAddWithVersion()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), "PERSONAL", 10)
        {
            Tags = ["tag1", "  TAG2 "]
        };
        var command = CreateCommand(request);
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

        await _assetStoreMock.Received(1).AddWithVersion(Arg.Any<Asset>(), Arg.Any<AssetVersion>(),
            Arg.Is<List<Tag>?>(tags => tags != null && tags.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagsMissing_ShouldReturnError()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), "PERSONAL", 10)
        {
            Tags = ["tag1", "nonexistent"]
        };
        var command = CreateCommand(request);
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
        await _assetStoreMock.DidNotReceiveWithAnyArgs().AddWithVersion(Arg.Any<Asset>(), Arg.Any<AssetVersion>(), Arg.Any<List<Tag>?>(), Arg.Any<CancellationToken>());
    }
}
