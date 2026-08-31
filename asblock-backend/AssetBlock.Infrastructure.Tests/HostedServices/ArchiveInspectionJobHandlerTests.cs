using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class ArchiveInspectionJobHandlerTests
{
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly IAssetStorageService _storageService = Substitute.For<IAssetStorageService>();
    private readonly IEncryptionService _encryptionService = Substitute.For<IEncryptionService>();
    private readonly IArchiveSafetyInspector _inspector = Substitute.For<IArchiveSafetyInspector>();
    private readonly IAssetProcessingLifecycleStore _lifecycleStore = Substitute.For<IAssetProcessingLifecycleStore>();
    private readonly ArchiveInspectionJobHandler _sut;

    public ArchiveInspectionJobHandlerTests()
    {
        _encryptionService.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Stream>(1).WriteAsync(new byte[] { 1, 2, 3 }, ci.Arg<CancellationToken>()).AsTask());
        _storageService.OpenRead(
                Arg.Any<string>(),
                Arg.Any<Func<Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<Stream, CancellationToken, Task>>()(new MemoryStream([1, 2, 3]), ci.Arg<CancellationToken>()));

        _sut = new ArchiveInspectionJobHandler(
            _assetStore,
            _storageService,
            _encryptionService,
            _inspector,
            _lifecycleStore,
            Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()),
            NullLogger<ArchiveInspectionJobHandler>.Instance);
    }

    [Fact]
    public async Task Process_WhenVersionNotFound_ShouldReturnTerminalFailure()
    {
        AssetProcessingJobContext<ArchiveInspectionPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns((AssetVersion?)null);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();
        var terminal = (AssetProcessingJobOutcome.TerminalFailure)outcome;
        terminal.ErrorCode.Should().Be("VERSION_NOT_FOUND");
    }

    [Fact]
    public async Task Process_WhenDecryptionFailsWithCryptographicException_ShouldTransitionRejected()
    {
        AssetProcessingJobContext<ArchiveInspectionPayload> context = CreateContext();
        AssetVersion version = CreateVersion(context.AssetVersionId);
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(version);

        _storageService.OpenRead(
            version.StorageKey,
            Arg.Any<Func<Stream, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => throw new CryptographicException("Decryption error"));

        _lifecycleStore.TransitionArchiveInspectionRejected(
            context.JobId,
            context.LeaseToken,
            context.AssetId,
            context.AssetVersionId,
            "ARCHIVE_CORRUPT",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        var committed = (AssetProcessingJobOutcome.AtomicCommitted)outcome;
        committed.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);
    }

    [Fact]
    public async Task Process_WhenArchiveSafe_ShouldTransitionAcceptedAndReturnCommitted()
    {
        AssetProcessingJobContext<ArchiveInspectionPayload> context = CreateContext();
        AssetVersion version = CreateVersion(context.AssetVersionId);
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(version);

        var safeResult = ArchiveSafetyResult.Safe(5, 1024, "# Readme", null);
        _inspector.Inspect(Arg.Any<Stream>(), version.FileName, Arg.Any<CancellationToken>())
            .Returns(safeResult);

        _lifecycleStore.TransitionArchiveInspectionAccepted(
            context.JobId,
            context.LeaseToken,
            context.AssetId,
            context.AssetVersionId,
            Arg.Any<ArchiveInspectionResult>(),
            Arg.Any<BoundedArchiveAnalysisRecord>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        var committed = (AssetProcessingJobOutcome.AtomicCommitted)outcome;
        committed.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
    }

    [Fact]
    public async Task Process_WhenArchiveUnsafe_ShouldTransitionRejectedAndReturnCommitted()
    {
        AssetProcessingJobContext<ArchiveInspectionPayload> context = CreateContext();
        AssetVersion version = CreateVersion(context.AssetVersionId);
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(version);

        var unsafeResult = ArchiveSafetyResult.Rejected("ARCHIVE_PATH_TRAVERSAL", "Path traversal detected");
        _inspector.Inspect(Arg.Any<Stream>(), version.FileName, Arg.Any<CancellationToken>())
            .Returns(unsafeResult);

        _lifecycleStore.TransitionArchiveInspectionRejected(
            context.JobId,
            context.LeaseToken,
            context.AssetId,
            context.AssetVersionId,
            "ARCHIVE_PATH_TRAVERSAL",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        var committed = (AssetProcessingJobOutcome.AtomicCommitted)outcome;
        committed.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);
    }

    private static AssetProcessingJobContext<ArchiveInspectionPayload> CreateContext() =>
        new(
            JobId: Guid.NewGuid(),
            LeaseToken: Guid.NewGuid(),
            AssetId: Guid.NewGuid(),
            AssetVersionId: Guid.NewGuid(),
            DefinitionVersion: 1,
            AttemptCount: 1,
            MaxAttempts: 3,
            Payload: new ArchiveInspectionPayload(),
            TraceParent: null,
            CancellationToken: CancellationToken.None);

    private static AssetVersion CreateVersion(Guid versionId) =>
        new()
        {
            Id = versionId,
            AssetId = Guid.NewGuid(),
            VersionNumber = 1,
            IsCurrent = false,
            StorageKey = "assets/test/file.zip",
            FileName = "file.zip",
            ContentLength = 1000,
            ContentSha256 = "abc123sha",
            ReleaseNotes = "Initial release",
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "MIT License",
            LicenseTerms = "Terms...",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = AssetVersionProcessingStatus.PENDING_INSPECTION,
            ProcessingUpdatedAt = DateTimeOffset.UtcNow
        };
}
