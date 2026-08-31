using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.UseCases.Assets.UploadAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;
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
    private readonly IUnitOfWork _unitOfWorkMock;
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
        IAssetProcessingJobStore processingJobStoreMock = Substitute.For<IAssetProcessingJobStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        _encryptionServiceMock.ComputeCiphertextLength(Arg.Any<long>()).Returns(4L);
        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UploadAssetCommandHandler(
            _categoryStoreMock,
            _assetStoreMock,
            _tagStoreMock,
            _assetStorageServiceMock,
            _encryptionServiceMock,
            new AssetEncryptUploadService(_encryptionServiceMock, _assetStorageServiceMock),
            processingJobStoreMock,
            Microsoft.Extensions.Options.Options.Create(new FileUploadOptions()),
            _unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<UploadAssetCommandHandler>.Instance);
    }

    private static UploadAssetRequest DefaultRequest(string title = "Title", string desc = "Desc", decimal price = 100m, string licenseCode = "PERSONAL") =>
        new(title, desc, price, Guid.NewGuid(), licenseCode);

    private static UploadAssetCommand CreateCommand(UploadAssetRequest request, string fileName = "test.zip", long length = 1) =>
        new(Guid.NewGuid(), request, new MemoryStream([1]), fileName, length);

    [Fact]
    public async Task Handle_WhenFileIsExactlyAtConfiguredLimit_ShouldAcceptUpload()
    {
        UploadAssetRequest request = DefaultRequest();
        UploadAssetCommand command = CreateCommand(request, length: 250L * 1024 * 1024);
        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>())
            .Returns(new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" });

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _encryptionServiceMock.Received(1).ComputeCiphertextLength(250L * 1024 * 1024);
    }

    [Fact]
    public async Task Handle_WhenCategoryNotFound_ShouldReturnNotFound()
    {
        UploadAssetRequest request = DefaultRequest();
        UploadAssetCommand command = CreateCommand(request);

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns((Category?)null);

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenEncryptionFails_ShouldReturnError()
    {
        UploadAssetRequest request = DefaultRequest();
        UploadAssetCommand command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _encryptionServiceMock.Encrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Encryption Error"));

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
        await _assetStorageServiceMock.Received(1).Delete(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenStorageUploadFails_ShouldReturnError()
    {
        UploadAssetRequest request = DefaultRequest();
        UploadAssetCommand command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _assetStorageServiceMock.Upload(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Storage Error"));

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ASSET_UPLOAD_FAILED);
        await _assetStorageServiceMock.Received(1).Delete(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenStreamingUploadSucceeds_ShouldPipeCiphertextWithoutSeekableBuffer()
    {
        UploadAssetRequest request = DefaultRequest();
        UploadAssetCommand command = CreateCommand(request);
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
                Stream output = callInfo.ArgAt<Stream>(1);
                await output.WriteAsync(ciphertext);
            });
        _assetStorageServiceMock.Upload(
                Arg.Any<string>(),
                Arg.Any<Stream>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                Stream input = callInfo.ArgAt<Stream>(1);
                uploadStreamCanSeek = input.CanSeek;
                await using var destination = new MemoryStream();
                await input.CopyToAsync(destination);
                uploaded = destination.ToArray();
            });

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        uploadStreamCanSeek.Should().BeFalse();
        uploaded.Should().Equal(ciphertext);
    }

    [Fact]
    public async Task Handle_WhenStreamingIsCancelled_ShouldPropagateCancellation()
    {
        UploadAssetRequest request = DefaultRequest();
        UploadAssetCommand command = CreateCommand(request);
        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>())
            .Returns(new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" });
        _encryptionServiceMock.Encrypt(
                Arg.Any<Stream>(),
                Arg.Any<Stream>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        Func<Task<Result<Guid>>> act = () => _handler.Handle(command, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _assetStorageServiceMock.Received(1).Delete(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _assetStoreMock.DidNotReceiveWithAnyArgs().AddWithVersion(
            Arg.Any<Asset>(),
            Arg.Any<AssetVersion>(),
            Arg.Any<List<Tag>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDbAddFails_ShouldNotDeleteStorageAndThrow()
    {
        UploadAssetRequest request = DefaultRequest();
        UploadAssetCommand command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _assetStoreMock.AddWithVersion(Arg.Any<Asset>(), Arg.Any<AssetVersion>(), Arg.Any<List<Tag>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB Error"));

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("DB Error");
        await _assetStorageServiceMock.DidNotReceive()
            .Delete(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDbPhaseIsCancelled_ShouldNotDeleteStorage()
    {
        UploadAssetRequest request = DefaultRequest();
        UploadAssetCommand command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);
        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        Func<Task<Result<Guid>>> act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _assetStorageServiceMock.DidNotReceive()
            .Delete(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnAssetIdClearCache()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), "PERSONAL", 10);
        UploadAssetCommand command = CreateCommand(request, fileName: "path/to/MyArchive.TAR.GZ");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();

        await _assetStoreMock.Received(1).AddWithVersion(
            Arg.Is<Asset>(a =>
                a.Id == result.Value &&
                a.Title == "Title" &&
                a.DownloadLimitPerHour == 10),
            Arg.Is<AssetVersion>(v =>
                v.FileName == "MyArchive.tar.gz" &&
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
    public async Task Handle_WhenMaliciousOrNonAsciiFileName_ShouldPersistSafeNormalizedDisplayFileName()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), "PERSONAL", 10);
        UploadAssetCommand command = CreateCommand(request, fileName: @"..\..\..\etc\кириллица_""test"".zip");
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).AddWithVersion(
            Arg.Any<Asset>(),
            Arg.Is<AssetVersion>(v => v.FileName == "test.zip"),
            Arg.Any<List<Tag>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTagsPresent_ShouldVerifyTagsAndCallAddWithVersion()
    {
        var request = new UploadAssetRequest("Title", "Desc", 100m, Guid.NewGuid(), "PERSONAL", 10)
        {
            Tags = ["tag1", "  TAG2 "]
        };
        UploadAssetCommand command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);

        var existingTags = new List<Tag>
        {
            new() { Id = Guid.NewGuid(), Name = "tag1" },
            new() { Id = Guid.NewGuid(), Name = "tag2" }
        };
        _tagStoreMock.GetTagsByNames(Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => existingTags);

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

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
        UploadAssetCommand command = CreateCommand(request);
        var category = new Category { Id = request.CategoryId, Name = "Cat", Slug = "cat" };

        _categoryStoreMock.GetById(request.CategoryId, Arg.Any<CancellationToken>()).Returns(category);

        var existingTags = new List<Tag>
        {
            new() { Id = Guid.NewGuid(), Name = "tag1" }
        };
        _tagStoreMock.GetTagsByNames(Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(existingTags);

        Result<Guid> result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_TAG_NOT_FOUND);
        await _assetStoreMock.DidNotReceiveWithAnyArgs().AddWithVersion(Arg.Any<Asset>(), Arg.Any<AssetVersion>(), Arg.Any<List<Tag>?>(), Arg.Any<CancellationToken>());
    }
}
