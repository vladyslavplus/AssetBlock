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
    public async Task AuthorizeDownload_whenAssetMissing_returnsNotFound()
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

        var r = await sut.AuthorizeDownload(Guid.NewGuid(), Guid.NewGuid());
        r.Status.Should().Be(AssetDownloadStatus.NOT_FOUND);
    }

    [Fact]
    public async Task AuthorizeDownload_whenNotAuthorAndNoPurchase_returnsForbidden()
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

        var r = await sut.AuthorizeDownload(asset.Id, Guid.NewGuid());
        r.Status.Should().Be(AssetDownloadStatus.FORBIDDEN);
    }

    [Fact]
    public async Task CopyDecrypted_whenAuthor_decryptsContent()
    {
        var userId = Guid.NewGuid();
        var asset = CreateAsset(userId, Guid.NewGuid());
        var encryption = CreateEncryption();
        await using var plain = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        await using var cipherMs = new MemoryStream();
        await encryption.Encrypt(plain, cipherMs);
        var cipherBytes = cipherMs.ToArray();

        var storage = Substitute.For<IAssetStorageService>();
        storage.OpenRead(asset.StorageKey, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var consumer = ci.Arg<Func<Stream, CancellationToken, Task>>();
                return consumer(new MemoryStream(cipherBytes), CancellationToken.None);
            });

        var sut = new DownloadService(
            Substitute.For<IAssetStore>(),
            Substitute.For<IPurchaseStore>(),
            storage,
            encryption,
            new MemoryCacheService());

        await using var destination = new MemoryStream();
        await sut.CopyDecrypted(asset.StorageKey, destination);
        Encoding.UTF8.GetString(destination.ToArray()).Should().Be("payload");
    }

    [Fact]
    public async Task CopyDecrypted_whenStorageReadIsCancelled_propagatesCancellation()
    {
        var storage = Substitute.For<IAssetStorageService>();
        storage.OpenRead(
                Arg.Any<string>(),
                Arg.Any<Func<Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new OperationCanceledException());
        var sut = new DownloadService(
            Substitute.For<IAssetStore>(),
            Substitute.For<IPurchaseStore>(),
            storage,
            CreateEncryption(),
            new MemoryCacheService());
        await using var destination = new MemoryStream();

        var act = () => sut.CopyDecrypted("key", destination, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AuthorizeDownload_rateLimited_afterTooManyDownloads()
    {
        var userId = Guid.NewGuid();
        var asset = CreateAsset(userId, Guid.NewGuid());
        asset.DownloadLimitPerHour = 1;

        var assetStore = Substitute.For<IAssetStore>();
        assetStore.GetById(asset.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Asset?>(asset));

        var sut = new DownloadService(
            assetStore,
            Substitute.For<IPurchaseStore>(),
            Substitute.For<IAssetStorageService>(),
            CreateEncryption(),
            new MemoryCacheService());

        (await sut.AuthorizeDownload(asset.Id, userId)).Status.Should().Be(AssetDownloadStatus.SUCCESS);
        (await sut.AuthorizeDownload(asset.Id, userId)).Status.Should().Be(AssetDownloadStatus.RATE_LIMITED);
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
