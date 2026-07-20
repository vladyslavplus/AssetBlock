using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.PublishAssetVersion;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public class PublishAssetVersionCommandHandlerTests
{
    private readonly IAssetStore _assetStoreMock = Substitute.For<IAssetStore>();
    private readonly IAssetStorageService _assetStorageServiceMock = Substitute.For<IAssetStorageService>();
    private readonly IEncryptionService _encryptionServiceMock = Substitute.For<IEncryptionService>();
    private readonly IAssetArchiveInspector _archiveInspectorMock = Substitute.For<IAssetArchiveInspector>();
    private readonly IUnitOfWork _unitOfWorkMock = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _auditWriterMock = Substitute.For<IAuditWriter>();
    private readonly ICacheService _cacheMock = Substitute.For<ICacheService>();
    private readonly PublishAssetVersionCommandHandler _handler;

    private static readonly Guid _assetId = Guid.NewGuid();
    private static readonly Guid _authorId = Guid.NewGuid();

    public PublishAssetVersionCommandHandlerTests()
    {
        _encryptionServiceMock.ComputeCiphertextLength(Arg.Any<long>()).Returns(4L);
        _archiveInspectorMock.Inspect(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        _assetStoreMock.PublishNextVersion(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetVersion>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<AssetVersion>());

        _handler = new PublishAssetVersionCommandHandler(
            _assetStoreMock,
            _assetStorageServiceMock,
            _encryptionServiceMock,
            _archiveInspectorMock,
            Microsoft.Extensions.Options.Options.Create(new FileUploadOptions()),
            _unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<PublishAssetVersionCommandHandler>.Instance);
    }

    private static PublishAssetVersionCommand CreateCommand(
        Guid? authorId = null,
        string license = "COMMERCIAL",
        string fileName = "next.zip",
        long length = 1) =>
        new(
            _assetId,
            authorId ?? _authorId,
            new PublishAssetVersionRequest(license, "Bug fixes"),
            new MemoryStream([1]),
            fileName,
            length);

    private void StubOwnedAsset(DateTimeOffset? deletedAt = null)
    {
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(new Asset
        {
            Id = _assetId,
            AuthorId = _authorId,
            CategoryId = Guid.NewGuid(),
            Title = "Owned",
            StorageKey = "seed",
            FileName = "seed.zip",
            DeletedAt = deletedAt
        });
    }

    [Fact]
    public async Task Handle_WhenAssetMissing_ShouldReturnNotFound()
    {
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenAssetSoftDeleted_ShouldReturnNotFound()
    {
        StubOwnedAsset(DateTimeOffset.UtcNow);

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        await _assetStorageServiceMock.DidNotReceiveWithAnyArgs()
            .Upload(null!, null!, 0, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenNotAuthor_ShouldReturnForbidden()
    {
        StubOwnedAsset();

        var result = await _handler.Handle(CreateCommand(authorId: Guid.NewGuid()), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Forbidden);
        result.Errors.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
    }

    [Fact]
    public async Task Handle_WhenLicenseInvalid_ShouldReturnValidationError()
    {
        StubOwnedAsset();

        var result = await _handler.Handle(CreateCommand(license: "CUSTOM"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_LICENSE_CODE_INVALID);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldPublishNextVersionAndInvalidateCache()
    {
        StubOwnedAsset();

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _assetStoreMock.Received(1).PublishNextVersion(
            _assetId,
            _authorId,
            Arg.Is<AssetVersion>(v =>
                v.FileName == "next.zip" &&
                v.LicenseCode == Domain.Core.Enums.AssetLicenseCode.COMMERCIAL &&
                v.ContentSha256.Length == 64),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDbPublishFails_ShouldDeleteOrphanBlobAndThrow()
    {
        StubOwnedAsset();
        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB fail"));

        var act = () => _handler.Handle(CreateCommand(), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("DB fail");
        await _assetStorageServiceMock.Received(1).Delete(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenArchiveRejected_ShouldNotUpload()
    {
        StubOwnedAsset();
        _archiveInspectorMock.Inspect(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArchiveRejectedException("bad"));

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_ARCHIVE_REJECTED);
        await _assetStorageServiceMock.DidNotReceiveWithAnyArgs()
            .Upload(null!, null!, 0, CancellationToken.None);
    }
}
