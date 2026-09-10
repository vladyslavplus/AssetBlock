using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class VectorSearchCapabilityTests
{
    [Fact]
    public async Task CheckCapability_WhenEmbeddingsDisabled_ReturnsDisabledWithoutDatabaseQuery()
    {
        IDbContextFactory<ApplicationDbContext> dbContextFactory = Substitute.For<IDbContextFactory<ApplicationDbContext>>();
        IOptions<EmbeddingOptions> options = Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Enabled = false
        });

        var capability = new VectorSearchCapability(
            dbContextFactory,
            options,
            NullLogger<VectorSearchCapability>.Instance);

        VectorSearchCapabilityResult result = await capability.CheckCapability();

        result.IsAvailable.Should().BeFalse();
        result.IsConfigEnabled.Should().BeFalse();
        result.HasExtension.Should().BeFalse();
        result.Reason.Should().Contain("disabled");

        // Verify no DB context was created or queried
        await dbContextFactory.DidNotReceive().CreateDbContextAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsVectorSearchAvailable_WhenEmbeddingsDisabled_ReturnsFalse()
    {
        IDbContextFactory<ApplicationDbContext> dbContextFactory = Substitute.For<IDbContextFactory<ApplicationDbContext>>();
        IOptions<EmbeddingOptions> options = Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Enabled = false
        });

        var capability = new VectorSearchCapability(
            dbContextFactory,
            options,
            NullLogger<VectorSearchCapability>.Instance);

        var isAvailable = await capability.IsVectorSearchAvailable();

        isAvailable.Should().BeFalse();
        await dbContextFactory.DidNotReceive().CreateDbContextAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckCapability_WhenCallerTokenCancelled_RethrowsOperationCanceledException()
    {
        IDbContextFactory<ApplicationDbContext> dbContextFactory = Substitute.For<IDbContextFactory<ApplicationDbContext>>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        dbContextFactory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns<Task<ApplicationDbContext>>(_ => throw new OperationCanceledException(cts.Token));

        IOptions<EmbeddingOptions> options = Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Enabled = true,
            Provider = "Ollama",
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "rev",
            Digest = "sha256:abc",
            Dimension = 768
        });

        var capability = new VectorSearchCapability(
            dbContextFactory,
            options,
            NullLogger<VectorSearchCapability>.Instance);

        Func<Task> act = async () => await capability.CheckCapability(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CheckCapability_WhenDatabaseThrowsException_ReturnsUnavailableWithoutThrowing()
    {
        IDbContextFactory<ApplicationDbContext> dbContextFactory = Substitute.For<IDbContextFactory<ApplicationDbContext>>();
        dbContextFactory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns<Task<ApplicationDbContext>>(_ => throw new InvalidOperationException("Connection failed"));

        IOptions<EmbeddingOptions> options = Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Enabled = true,
            Provider = "Ollama",
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "rev",
            Digest = "sha256:abc",
            Dimension = 768
        });

        var capability = new VectorSearchCapability(
            dbContextFactory,
            options,
            NullLogger<VectorSearchCapability>.Instance);

        VectorSearchCapabilityResult result = await capability.CheckCapability();

        result.IsAvailable.Should().BeFalse();
        result.HasExtension.Should().BeFalse();
        result.Reason.Should().Contain("Connection failed");
    }
}
