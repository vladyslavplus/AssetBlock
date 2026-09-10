using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class EmbeddingGenerationJobHandlerTests : IAsyncDisposable
{
    private readonly SqliteDbContextHolder _holder;
    private readonly ApplicationDbContext _dbContext;
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly IAssetEmbeddingFinalizer _finalizer = Substitute.For<IAssetEmbeddingFinalizer>();
    private readonly ITextEmbeddingGenerator _generator = Substitute.For<ITextEmbeddingGenerator>();
    private readonly EmbeddingOptions _options;
    private readonly EmbeddingGenerationJobHandler _sut;

    public EmbeddingGenerationJobHandlerTests()
    {
        _holder = new SqliteDbContextHolder();
        _dbContext = _holder.Context;

        _options = new EmbeddingOptions
        {
            Enabled = true,
            Provider = "Ollama",
            BaseUrl = "http://127.0.0.1:11434",
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Digest = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Dimension = 768,
            ContentSchemaVersion = "asset-public-metadata-v1",
            RequestTimeoutSeconds = 10,
            MaxInputChars = 8192
        };

        _generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, _options.Digest));
        _finalizer.MarkJobNoOp(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _sut = new EmbeddingGenerationJobHandler(
            _assetStore,
            _generator,
            _finalizer,
            Microsoft.Extensions.Options.Options.Create(_options),
            _dbContext,
            NullLogger<EmbeddingGenerationJobHandler>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _holder.DisposeAsync();
    }

    [Fact]
    public async Task Process_WhenCancelled_ShouldRethrow()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext();

        Func<Task<AssetProcessingJobOutcome>> act = async () => await _sut.Process(context, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _finalizer.DidNotReceive().Finalize(Arg.Any<FinalizeEmbeddingParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenDisabled_ShouldReturnRetryableWithoutCallingGenerator()
    {
        _options.Enabled = false;
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext();

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();
        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenModelKeyMismatch_ShouldBeTerminal()
    {
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(modelKey: "wrong-model-key");

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();
        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenContentSchemaVersionMismatch_ShouldBeTerminal()
    {
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(schemaVersion: "asset-public-metadata-v999");

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();
        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenAssetDeleted_ShouldMarkNoOpAndSucceed()
    {
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext();
        _assetStore.GetById(context.AssetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = context.AssetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                Title = "Test",
                DeletedAt = DateTimeOffset.UtcNow
            });

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await _finalizer.Received(1).MarkJobNoOp(context.JobId, context.LeaseToken, Arg.Any<CancellationToken>());
        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenRevisionIsNewerThanTarget_ShouldMarkNoOpAndSucceed()
    {
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(targetRevision: 1);
        _assetStore.GetById(context.AssetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = context.AssetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                Title = "Test",
                SearchRevision = 2 // newer
            });

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await _finalizer.Received(1).MarkJobNoOp(context.JobId, context.LeaseToken, Arg.Any<CancellationToken>());
        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenNoReadyVersion_ShouldMarkNoOpAndSucceed()
    {
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext();
        _assetStore.GetById(context.AssetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = context.AssetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                Title = "Test",
                SearchRevision = 1
            });
        // No ready version in _dbContext

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await _finalizer.Received(1).MarkJobNoOp(context.JobId, context.LeaseToken, Arg.Any<CancellationToken>());
        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenContentHashMismatch_ShouldMarkNoOpAndSucceed()
    {
        var assetId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedAssetAndVersion(assetId, categoryId, "Category A");

        _assetStore.GetById(assetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = assetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = categoryId,
                Title = "Different Title Now",
                SearchRevision = 1
            });

        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(assetId: assetId, contentHash: "stale-hash");

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await _finalizer.Received(1).MarkJobNoOp(context.JobId, context.LeaseToken, Arg.Any<CancellationToken>());
        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenAssetDeleted_AndLeaseLost_ShouldReturnLeaseLost()
    {
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext();
        _assetStore.GetById(context.AssetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = context.AssetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                Title = "Test",
                DeletedAt = DateTimeOffset.UtcNow
            });
        _finalizer.MarkJobNoOp(context.JobId, context.LeaseToken, Arg.Any<CancellationToken>())
            .Returns(false);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();
        var retryable = (AssetProcessingJobOutcome.RetryableFailure)outcome;
        retryable.ErrorCode.Should().Be(ErrorCodes.LEASE_LOST);
    }

    [Fact]
    public async Task Process_WhenRevisionIsNewerThanTarget_AndLeaseLost_ShouldReturnLeaseLost()
    {
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(targetRevision: 1);
        _assetStore.GetById(context.AssetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = context.AssetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                Title = "Test",
                SearchRevision = 2
            });
        _finalizer.MarkJobNoOp(context.JobId, context.LeaseToken, Arg.Any<CancellationToken>())
            .Returns(false);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();
        var retryable = (AssetProcessingJobOutcome.RetryableFailure)outcome;
        retryable.ErrorCode.Should().Be(ErrorCodes.LEASE_LOST);
    }

    [Fact]
    public async Task Process_WhenNoReadyVersion_AndLeaseLost_ShouldReturnLeaseLost()
    {
        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext();
        _assetStore.GetById(context.AssetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = context.AssetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                Title = "Test",
                SearchRevision = 1
            });
        _finalizer.MarkJobNoOp(context.JobId, context.LeaseToken, Arg.Any<CancellationToken>())
            .Returns(false);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();
        var retryable = (AssetProcessingJobOutcome.RetryableFailure)outcome;
        retryable.ErrorCode.Should().Be(ErrorCodes.LEASE_LOST);
    }

    [Fact]
    public async Task Process_WhenContentHashMismatch_AndLeaseLost_ShouldReturnLeaseLost()
    {
        var assetId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedAssetAndVersion(assetId, categoryId, "Category A");

        _assetStore.GetById(assetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = assetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = categoryId,
                Title = "Different Title Now",
                SearchRevision = 1
            });
        _finalizer.MarkJobNoOp(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(assetId: assetId, contentHash: "stale-hash");

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();
        var retryable = (AssetProcessingJobOutcome.RetryableFailure)outcome;
        retryable.ErrorCode.Should().Be(ErrorCodes.LEASE_LOST);
    }

    [Fact]
    public async Task Process_WhenRuntimeModelAvailabilityFails_ShouldReturnRetryableWithoutGenerateOrFinalize()
    {
        var assetId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedAssetAndVersion(assetId, categoryId, "3D Assets");

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize("Sword", null, "3D Assets", null);

        _assetStore.GetById(assetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = assetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = categoryId,
                Title = "Sword",
                SearchRevision = 1
            });

        _generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(false, "Digest mismatch detected at runtime"));

        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(
            assetId: assetId,
            contentHash: canonical.ContentHash,
            targetRevision: 1);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();
        var retryable = (AssetProcessingJobOutcome.RetryableFailure)outcome;
        retryable.ErrorCode.Should().Be(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE);

        await _generator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _finalizer.DidNotReceive().Finalize(Arg.Any<FinalizeEmbeddingParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenProviderFails_ShouldReturnRetryable()
    {
        var assetId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedAssetAndVersion(assetId, categoryId, "3D Assets");

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize("Sword", null, "3D Assets", null);

        _assetStore.GetById(assetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = assetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = categoryId,
                Title = "Sword",
                SearchRevision = 1
            });

        _generator.Generate(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbedding>>(_ => Task.FromException<GeneratedEmbedding>(new HttpRequestException("Connection refused")));

        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(assetId: assetId, contentHash: canonical.ContentHash);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();
    }

    [Fact]
    public async Task Process_WhenSuccessful_ShouldFinalizeAndReturnCommittedSucceeded()
    {
        var assetId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedAssetAndVersion(assetId, categoryId, "3D Assets");

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize("Sword", null, "3D Assets", null);

        _assetStore.GetById(assetId, includeDeleted: true, Arg.Any<CancellationToken>())
            .Returns(new Asset
            {
                Id = assetId,
                AuthorId = Guid.NewGuid(),
                CategoryId = categoryId,
                Title = "Sword",
                SearchRevision = 1
            });

        var mockVector = new float[768];
        mockVector[0] = 1.0f;
        _generator.Generate(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbedding(mockVector, EmbeddingModelKey.Compute(_options), 768));

        _finalizer.Finalize(Arg.Any<FinalizeEmbeddingParameters>(), Arg.Any<CancellationToken>())
            .Returns(EmbeddingFinalizationStatus.COMMITTED);

        AssetProcessingJobContext<EmbeddingGenerationPayload> context = CreateContext(assetId: assetId, contentHash: canonical.ContentHash);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await _finalizer.Received(1).Finalize(Arg.Is<FinalizeEmbeddingParameters>(p =>
            p.AssetId == assetId &&
            p.SourceRevision == 1 &&
            p.ContentHash == canonical.ContentHash), Arg.Any<CancellationToken>());
    }

    private async Task SeedAssetAndVersion(Guid assetId, Guid categoryId, string categoryName)
    {
        var authorId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = authorId,
            Username = "user_" + Guid.NewGuid().ToString("N")[..8],
            Email = Guid.NewGuid().ToString("N")[..8] + "@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        _dbContext.Categories.Add(new Category
        {
            Id = categoryId,
            Name = categoryName,
            Slug = categoryName.ToLowerInvariant().Replace(' ', '-'),
            CreatedAt = DateTimeOffset.UtcNow
        });
        _dbContext.Assets.Add(new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "Asset Title",
            Price = 10m,
            CreatedAt = DateTimeOffset.UtcNow
        });
        _dbContext.AssetVersions.Add(new AssetVersion
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "key",
            FileName = "file",
            ContentLength = 100,
            ContentSha256 = "abc",
            ReleaseNotes = "notes",
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "Terms",
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
    }

    private AssetProcessingJobContext<EmbeddingGenerationPayload> CreateContext(
        Guid? assetId = null,
        long targetRevision = 1,
        string? contentHash = null,
        string? modelKey = null,
        string? schemaVersion = null)
    {
        Guid resolvedAssetId = assetId ?? Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var resolvedModelKey = modelKey ?? EmbeddingModelKey.Compute(_options);
        var resolvedContentHash = contentHash ?? "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var resolvedSchema = schemaVersion ?? AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION;

        EmbeddingGenerationPayload payload = new(
            resolvedAssetId,
            versionId,
            targetRevision,
            resolvedContentHash,
            resolvedModelKey,
            resolvedSchema);

        return new AssetProcessingJobContext<EmbeddingGenerationPayload>(
            Guid.NewGuid(),
            Guid.NewGuid(),
            resolvedAssetId,
            versionId,
            1,
            1,
            3,
            payload,
            null,
            CancellationToken.None);
    }
}
