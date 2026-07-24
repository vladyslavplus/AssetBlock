using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
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
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.FORBIDDEN);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenUserIsAuthor_ShouldNotCheckPurchase_AndSucceed()
    {
        var asset = MakeAsset(authorId: _userId, downloadLimit: null);
        var currentVersionId = Guid.NewGuid();
        const string storageKey = "assets/test/file.bin";
        const string fileName = "file.zip";
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _assetStoreMock.GetCurrentVersionSnapshot(_assetId, Arg.Any<CancellationToken>())
            .Returns(MakeSnapshot(currentVersionId, storageKey, fileName));

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        result.Permit.Should().NotBeNull();
        result.Permit!.StorageKey.Should().Be(storageKey);
        result.Permit.FileName.Should().Be(fileName);
        await _purchaseStoreMock.DidNotReceiveWithAnyArgs().GetPurchase(Guid.Empty, Guid.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenUserHasPurchase_ShouldSucceed()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        var versionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, versionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        StubPurchaserEntitledVersion(versionId);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        result.Permit.Should().NotBeNull();
    }

    [Fact]
    public async Task AuthorizeDownload_WhenUnderRateLimit_ShouldSucceed()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        var versionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, versionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        StubPurchaserEntitledVersion(versionId);
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(4L);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenAtExactRateLimit_ShouldSucceed()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        var versionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, versionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        StubPurchaserEntitledVersion(versionId);
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(limit);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenRateLimitExceeded_ShouldReturnRateLimited()
    {
        const int limit = 5;
        var asset = MakeAsset(authorId: _authorId, downloadLimit: limit);
        var versionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, versionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        StubPurchaserEntitledVersion(versionId);
        _cacheMock.Increment(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(limit + 1L);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.RATE_LIMITED);
    }

    [Fact]
    public async Task AuthorizeDownload_WhenNoDownloadLimit_ShouldNeverCallCacheIncrement()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        var versionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, versionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        StubPurchaserEntitledVersion(versionId);

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        await _cacheMock.DidNotReceiveWithAnyArgs()
            .Increment(null!, TimeSpan.Zero, CancellationToken.None);
    }

    [Fact]
    public async Task AuthorizeDownload_RateLimitKey_MustIncludeAssetIdAndUserId()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: 10);
        var versionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, versionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        StubPurchaserEntitledVersion(versionId);
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

    [Fact]
    public async Task AuthorizeDownload_Author_CanDownloadSpecificOlderVersion()
    {
        var asset = MakeAsset(authorId: _userId, downloadLimit: null);
        var versionId = Guid.NewGuid();
        var version = MakeVersion(versionId, versionNumber: 1, storageKey: "assets/v1.bin", fileName: "v1.zip");
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _assetStoreMock.GetVersion(_assetId, versionId, Arg.Any<CancellationToken>()).Returns(version);

        var result = await _service.AuthorizeDownload(_assetId, _userId, versionId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        result.Permit!.StorageKey.Should().Be("assets/v1.bin");
        result.Permit.FileName.Should().Be("v1.zip");
    }

    [Fact]
    public async Task AuthorizeDownload_Purchaser_CanDownloadPurchasedAndLaterVersions()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        var purchasedVersionId = Guid.NewGuid();
        var laterVersionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, purchasedVersionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _assetStoreMock.GetVersion(_assetId, purchasedVersionId, Arg.Any<CancellationToken>())
            .Returns(MakeVersion(purchasedVersionId, 2, "assets/v2.bin", "v2.zip"));
        _assetStoreMock.GetVersion(_assetId, laterVersionId, Arg.Any<CancellationToken>())
            .Returns(MakeVersion(laterVersionId, 3, "assets/v3.bin", "v3.zip"));

        var purchased = await _service.AuthorizeDownload(_assetId, _userId, purchasedVersionId);
        var later = await _service.AuthorizeDownload(_assetId, _userId, laterVersionId);

        purchased.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        purchased.Permit!.StorageKey.Should().Be("assets/v2.bin");
        later.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        later.Permit!.StorageKey.Should().Be("assets/v3.bin");
    }

    [Fact]
    public async Task AuthorizeDownload_Purchaser_CannotDownloadPrePurchaseVersion()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        var purchasedVersionId = Guid.NewGuid();
        var olderVersionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, purchasedVersionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _assetStoreMock.GetVersion(_assetId, purchasedVersionId, Arg.Any<CancellationToken>())
            .Returns(MakeVersion(purchasedVersionId, 2, "assets/v2.bin", "v2.zip"));
        _assetStoreMock.GetVersion(_assetId, olderVersionId, Arg.Any<CancellationToken>())
            .Returns(MakeVersion(olderVersionId, 1, "assets/v1.bin", "v1.zip"));

        var result = await _service.AuthorizeDownload(_assetId, _userId, olderVersionId);

        result.Status.Should().Be(AssetDownloadStatus.FORBIDDEN);
        result.Permit.Should().BeNull();
    }

    [Fact]
    public async Task AuthorizeDownload_Default_ServesLatestEntitledCurrentVersion()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        var purchasedVersionId = Guid.NewGuid();
        var currentVersionId = Guid.NewGuid();
        var purchase = MakePurchase(_userId, _assetId, purchasedVersionId);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns(purchase);
        _assetStoreMock.GetVersion(_assetId, purchasedVersionId, Arg.Any<CancellationToken>())
            .Returns(MakeVersion(purchasedVersionId, 1, "assets/v1.bin", "v1.zip"));
        _assetStoreMock.GetCurrentVersionSnapshot(_assetId, Arg.Any<CancellationToken>())
            .Returns(new AssetCurrentVersionSnapshot(
                _assetId,
                currentVersionId,
                _authorId,
                "Test",
                null,
                9.99m,
                null,
                2,
                DateTimeOffset.UtcNow,
                "v2.zip",
                "assets/v2.bin",
                10,
                new string('b', 64),
                "PERSONAL",
                "1.0",
                "Personal use",
                "terms"));

        var result = await _service.AuthorizeDownload(_assetId, _userId);

        result.Status.Should().Be(AssetDownloadStatus.SUCCESS);
        result.Permit!.StorageKey.Should().Be("assets/v2.bin");
        result.Permit.FileName.Should().Be("v2.zip");
    }

    [Fact]
    public async Task AuthorizeDownload_UnrelatedUser_IsForbidden()
    {
        var asset = MakeAsset(authorId: _authorId, downloadLimit: null);
        _assetStoreMock.GetById(_assetId, Arg.Any<CancellationToken>()).Returns(asset);
        _purchaseStoreMock.GetPurchase(_userId, _assetId, Arg.Any<CancellationToken>()).Returns((Purchase?)null);

        var result = await _service.AuthorizeDownload(_assetId, _userId, Guid.NewGuid());

        result.Status.Should().Be(AssetDownloadStatus.FORBIDDEN);
    }

    private static Asset MakeAsset(Guid authorId, int? downloadLimit) => new Asset
    {
        Id = _assetId,
        AuthorId = authorId,
        CategoryId = Guid.NewGuid(),
        Title = "Test Asset",
        Price = 9.99m,
        DownloadLimitPerHour = downloadLimit,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private void StubPurchaserEntitledVersion(
        Guid assetVersionId,
        string storageKey = "assets/test/file.bin",
        string fileName = "file.zip",
        int versionNumber = 1)
    {
        _assetStoreMock.GetVersion(_assetId, assetVersionId, Arg.Any<CancellationToken>())
            .Returns(MakeVersion(assetVersionId, versionNumber, storageKey, fileName));
        _assetStoreMock.GetCurrentVersionSnapshot(_assetId, Arg.Any<CancellationToken>())
            .Returns(MakeSnapshot(assetVersionId, storageKey, fileName, versionNumber));
    }

    private static Purchase MakePurchase(
        Guid userId,
        Guid assetId,
        Guid assetVersionId,
        decimal pricePaid = 9.99m,
        string currency = "usd") => new Purchase
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AssetId = assetId,
        AssetVersionId = assetVersionId,
        CheckoutIntentId = Guid.NewGuid(),
        PricePaid = pricePaid,
        Currency = currency,
        StripePaymentId = "cs_test",
        PurchasedAt = DateTimeOffset.UtcNow
    };

    private static AssetCurrentVersionSnapshot MakeSnapshot(
        Guid versionId,
        string storageKey,
        string fileName,
        int versionNumber = 1) =>
        new(
            _assetId,
            versionId,
            _authorId,
            "Test Asset",
            null,
            9.99m,
            null,
            versionNumber,
            DateTimeOffset.UtcNow,
            fileName,
            storageKey,
            10,
            new string('b', 64),
            "PERSONAL",
            "1.0",
            "Personal use",
            "terms");

    private static AssetVersion MakeVersion(Guid id, int versionNumber, string storageKey, string fileName) => new()
    {
        Id = id,
        AssetId = _assetId,
        VersionNumber = versionNumber,
        IsCurrent = false,
        StorageKey = storageKey,
        FileName = fileName,
        ContentLength = 1,
        ContentSha256 = new string('a', 64),
        ReleaseNotes = "Initial release",
        LicenseCode = Domain.Core.Enums.AssetLicenseCode.PERSONAL,
        LicenseTemplateVersion = "1.0",
        LicenseDisplayName = "Personal use",
        LicenseTerms = "terms",
        CreatedAt = DateTimeOffset.UtcNow
    };
}
