using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class DownloadServiceTests
{
    [Fact]
    public async Task GetAssetStream_whenAssetMissing_returnsNotFound()
    {
        var assetStore = Substitute.For<IAssetStore>();
        assetStore.GetById(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(null));

        var sut = new DownloadService(
            assetStore,
            Substitute.For<IPurchaseStore>(),
            Substitute.For<IAssetStorageService>(),
            CreateEncryption(),
            new MemoryCacheService());

        var r = await sut.GetAssetStream(Guid.NewGuid(), Guid.NewGuid());
        r.Status.Should().Be(AssetDownloadStatus.NotFound);
    }

    [Fact]
    public async Task GetAssetStream_whenNotAuthorAndNoPurchase_returnsForbidden()
    {
        var asset = CreateAsset(Guid.NewGuid(), Guid.NewGuid());
        var assetStore = Substitute.For<IAssetStore>();
        assetStore.GetById(asset.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Asset?>(asset));

        var purchaseStore = Substitute.For<IPurchaseStore>();
        purchaseStore.Exists(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var sut = new DownloadService(
            assetStore,
            purchaseStore,
            Substitute.For<IAssetStorageService>(),
            CreateEncryption(),
            new MemoryCacheService());

        var r = await sut.GetAssetStream(asset.Id, Guid.NewGuid());
        r.Status.Should().Be(AssetDownloadStatus.Forbidden);
    }

    [Fact]
    public async Task GetAssetStream_whenAuthor_decryptsContent()
    {
        var userId = Guid.NewGuid();
        var asset = CreateAsset(userId, Guid.NewGuid());
        var encryption = CreateEncryption();
        await using var plain = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        await using var cipherMs = new MemoryStream();
        await encryption.Encrypt(plain, cipherMs);
        var cipherBytes = cipherMs.ToArray();

        var storage = Substitute.For<IAssetStorageService>();
        storage.Get(asset.StorageKey, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(cipherBytes)));

        var assetStore = Substitute.For<IAssetStore>();
        assetStore.GetById(asset.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Asset?>(asset));

        var sut = new DownloadService(
            assetStore,
            Substitute.For<IPurchaseStore>(),
            storage,
            encryption,
            new MemoryCacheService());

        var r = await sut.GetAssetStream(asset.Id, userId);
        r.Status.Should().Be(AssetDownloadStatus.Success);
        r.Content.Should().NotBeNull();
        using var reader = new StreamReader(r.Content!, leaveOpen: false);
        (await reader.ReadToEndAsync()).Should().Be("payload");
    }

    [Fact]
    public async Task GetAssetStream_rateLimited_afterTooManyDownloads()
    {
        var userId = Guid.NewGuid();
        var asset = CreateAsset(userId, Guid.NewGuid());
        asset.DownloadLimitPerHour = 1;

        var encryption = CreateEncryption();
        await using var plain = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        await using var cipherMs = new MemoryStream();
        await encryption.Encrypt(plain, cipherMs);

        var storage = Substitute.For<IAssetStorageService>();
        storage.Get(asset.StorageKey, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(cipherMs.ToArray())));

        var assetStore = Substitute.For<IAssetStore>();
        assetStore.GetById(asset.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Asset?>(asset));

        var sut = new DownloadService(
            assetStore,
            Substitute.For<IPurchaseStore>(),
            storage,
            encryption,
            new MemoryCacheService());

        (await sut.GetAssetStream(asset.Id, userId)).Status.Should().Be(AssetDownloadStatus.Success);
        (await sut.GetAssetStream(asset.Id, userId)).Status.Should().Be(AssetDownloadStatus.RateLimited);
    }

    private static Asset CreateAsset(Guid authorId, Guid categoryId) =>
        new()
        {
            Id = Guid.NewGuid(),
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "t",
            StorageKey = "sk",
            FileName = "f.bin",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static AesGcmEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new AesGcmEncryptionService(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions { KeyBase64 = key }));
    }
}
