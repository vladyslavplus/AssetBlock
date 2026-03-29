using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class StripePaymentServiceTests
{
    [Fact]
    public async Task CreateCheckoutSession_throws_whenSuccessAndCancelUrlsMissing()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "sk_test_123456789012345678901234567890",
            DefaultSuccessUrl = "",
            DefaultCancelUrl = ""
        });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new StripePaymentService(
            opts,
            Substitute.For<IAssetStore>(),
            Substitute.For<IPurchaseStore>(),
            resilience,
            NullLogger<StripePaymentService>.Instance);

        var act = async () =>
            await sut.CreateCheckoutSession(Guid.NewGuid(), Guid.NewGuid(), null, null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateCheckoutSession_failsFast_whenStripeApiUnreachable()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "sk_test_123456789012345678901234567890",
            DefaultSuccessUrl = "https://example.com/success",
            DefaultCancelUrl = "https://example.com/cancel"
        });
        var assetStore = Substitute.For<IAssetStore>();
        assetStore.GetById(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = Guid.NewGuid(),
                AuthorId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                Title = "Asset title",
                Description = "Asset description",
                Price = 12.50m,
                StorageKey = "key",
                FileName = "file.zip",
                CreatedAt = DateTimeOffset.UtcNow
            });

        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new StripePaymentService(
            opts,
            assetStore,
            Substitute.For<IPurchaseStore>(),
            resilience,
            NullLogger<StripePaymentService>.Instance);

        var act = async () =>
            await sut.CreateCheckoutSession(Guid.NewGuid(), Guid.NewGuid(), null, null);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task HandleCheckoutCompleted_returnsNull_whenWebhookSecretMissing()
    {
        var sut = CreateSut(webhookSecret: "");
        var r = await sut.HandleCheckoutCompleted("{}", "sig");
        r.Should().BeNull();
    }

    [Fact]
    public async Task HandleCheckoutCompleted_returnsNull_whenPayloadInvalid()
    {
        var sut = CreateSut(webhookSecret: "whsec_test_secret");
        var r = await sut.HandleCheckoutCompleted("not-json", "bad_sig");
        r.Should().BeNull();
    }

    private static StripePaymentService CreateSut(string webhookSecret)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "sk_test_123456789012345678901234567890",
            WebhookSecret = webhookSecret
        });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        return new StripePaymentService(
            opts,
            Substitute.For<IAssetStore>(),
            Substitute.For<IPurchaseStore>(),
            resilience,
            NullLogger<StripePaymentService>.Instance);
    }
}
