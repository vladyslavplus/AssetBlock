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
    public async Task GetAssetStream_WhenAssetNotFound_ShouldReturnNotFound()
    {
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns((Asset?)null);

        var result = await _service.GetAssetStream(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.NotFound);
        result.Content.Should().BeNull();
    }

    [Fact]
    public async Task GetAssetStream_WhenUserHasNoPurchaseAndIsNotAuthor_ShouldReturnForbidden()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.GetAssetStream(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.Forbidden);
    }

    [Fact]
    public async Task GetAssetStream_WhenUserIsAuthor_ShouldNotCheckPurchase_AndProceed()
    {
        var asset = MakeAsset(authorId: _userId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        // No purchase setup — author should bypass the purchase check
        SetupSuccessfulDecrypt();

        var result = await _service.GetAssetStream(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.Success);
    }

    [Fact]
    public async Task GetAssetStream_WhenUserHasPurchase_ShouldProceed()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        SetupSuccessfulDecrypt();

        var result = await _service.GetAssetStream(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.Success);
    }

    [Fact]
    public async Task GetAssetStream_WhenUnderRateLimit_ShouldProceed()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        // Simulate counter returning 4 (below limit of 5) → not rate limited
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(4L);
        SetupSuccessfulDecrypt();

        var result = await _service.GetAssetStream(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.Success);
    }

    [Fact]
    public async Task GetAssetStream_WhenAtExactRateLimit_ShouldProceed()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        // Count == limit → still allowed (blocked only when count > limit)
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(limit);
        SetupSuccessfulDecrypt();

        var result = await _service.GetAssetStream(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.Success);
    }

    [Fact]
    public async Task GetAssetStream_WhenRateLimitExceeded_ShouldReturnRateLimited()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        // Count == limit + 1 → blocked
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(limit + 1L);

        var result = await _service.GetAssetStream(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.RateLimited);
    }

    [Fact]
    public async Task GetAssetStream_WhenNoDownloadLimit_ShouldNeverCallCacheIncrement()
    {
        // Asset has no download limit → rate limiting is skipped entirely
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        SetupSuccessfulDecrypt();

        var result = await _service.GetAssetStream(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.Success);
        await _cacheMock.DidNotReceiveWithAnyArgs()
            .Increment(null!, TimeSpan.Zero, CancellationToken.None);
    }

    [Fact]
    public async Task GetAssetStream_RateLimitKey_MustIncludeAssetIdAndUserId()
    {
        // Ensures the rate limit cache key is partitioned per (asset, user) — not globally
        var asset = MakeAsset(authorId: _authorId, downloadLimit: 10);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.Exists(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(true);
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(1L);
        SetupSuccessfulDecrypt();

        await _service.GetAssetStream(_assetId, _userId);

        await _cacheMock.Received(1).Increment(
            Arg.Is<string>(key => key.Contains(_assetId.ToString()) && key.Contains(_userId.ToString())),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
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

    private void SetupSuccessfulDecrypt()
    {
        // Return a valid readable stream from storage and write decrypted content to output
        _assetStorageServiceMock.Get(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream([1, 2, 3]));

        _encryptionServiceMock
            .Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }
}
