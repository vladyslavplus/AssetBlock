using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.Services;

/// <summary>
/// Unit tests for DownloadService — the core access control and rate-limiting engine.
/// </summary>
public class DownloadServiceTests
{
    private readonly IAssetStore _assetStoreMock;
    private readonly IPurchaseStore _purchaseStoreMock;
    private readonly IAssetStorageService _assetStorageServiceMock;
    private readonly IEncryptionService _encryptionServiceMock;
    private readonly ICacheService _cacheMock;
    private readonly IDownloadService _service;

    private static readonly Guid _userId = Guid.NewGuid();
    private static readonly Guid _authorId = Guid.NewGuid();
    private static readonly Guid _assetId = Guid.NewGuid();

    public DownloadServiceTests()
    {
        _assetStoreMock = Substitute.For<IAssetStore>();
        _purchaseStoreMock = Substitute.For<IPurchaseStore>();
        _assetStorageServiceMock = Substitute.For<IAssetStorageService>();
        _encryptionServiceMock = Substitute.For<IEncryptionService>();
        _cacheMock = Substitute.For<ICacheService>();

        _service = new DownloadService(
            _assetStoreMock,
            _purchaseStoreMock,
            _assetStorageServiceMock,
            _encryptionServiceMock,
            _cacheMock);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenAssetNotFound_ShouldReturnNotFound()
    {
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.NOT_FOUND);
        result.Permit.Should().BeNull();
    }

    [Fact]
    public async Task AuthorizeDownload_WhenUserHasNoPurchaseAndIsNotAuthor_ShouldReturnForbidden()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.FORBIDDEN);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenUserIsAuthor_ShouldNotCheckPurchase_AndSucceed()
    {
        var asset = MakeAsset(authorId: _userId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        result.Permit.Should().NotBeNull();
        result.Permit!.StorageKey.Should().Be(asset.StorageKey);
        result.Permit.FileName.Should().Be(asset.FileName);
        await _purchaseStoreMock.DidNotReceiveWithAnyArgs().Exists(Guid.Empty, Guid.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenUserHasPurchase_ShouldSucceed()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        result.Permit.Should().NotBeNull();
    }

    [Fact]
    public async Task AuthorizeDownload_WhenUnderRateLimit_ShouldSucceed()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(4L);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenAtExactRateLimit_ShouldSucceed()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(limit);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenRateLimitExceeded_ShouldReturnRateLimited()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(limit + 1L);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.RATE_LIMITED);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenNoDownloadLimit_ShouldNeverCallCacheIncrement()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        await _cacheMock.DidNotReceiveWithAnyArgs()
            .Increment(null!, TimeSpan.Zero, CancellationToken.None);
    }

    [Fact]
    public async Task AuthorizeDownload_RateLimitKey_MustIncludeAssetIdAndUserId()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: 10);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(1L);

        await _service.AuthorizeDownload(_assetId, _userId);

        await _cacheMock.Received(1).Increment(
            Arg.Is<string>(key => key.Contains(_assetId.ToString()) && key.Contains(_userId.ToString())),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CopyDecrypted_ShouldOpenReadAndDecryptToDestination()
    {
        _assetStorageServiceMock
            .OpenRead(Arg.Any<string>(), Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var consumer = ci.Arg<Func<Stream, CancellationToken, Task>>();
                return consumer(new MemoryStream([1, 2, 3]), CancellationToken.None);
            });

        await using var destination = new MemoryStream();
        await _service.CopyDecrypted("assets/k", destination);

        await _encryptionServiceMock.Received(1)
            .Decrypt(Arg.Any<Stream>(), destination, Arg.Any<CancellationToken>());
    }

    private static Asset MakeAsset(Guid authorId, int? downloadLimit) => new Asset
    {
        Id = _assetId,
        AuthorId = authorId,
        CategoryId = Guid.NewGuid(),
        Title = "Test Asset",
        Price = 9.99m,
        StorageKey = "assets/test/file.bin",
        FileName = "file.zip",
        DownloadLimitPerHour = downloadLimit,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
