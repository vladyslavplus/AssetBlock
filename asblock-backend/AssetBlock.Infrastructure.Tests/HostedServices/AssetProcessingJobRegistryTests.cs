using System.Reflection;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class AssetProcessingJobRegistryTests
{
    private sealed class TestInspectionHandler : IAssetProcessingJobHandler<ArchiveInspectionPayload, ArchiveInspectionResult>
    {
        public Task<AssetProcessingJobOutcome> Process(
            AssetProcessingJobContext<ArchiveInspectionPayload> context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(10, 2048)));
        }
    }

    private sealed class WrongResultHandler : IAssetProcessingJobHandler<ArchiveInspectionPayload, ArchiveInspectionResult>
    {
        public Task<AssetProcessingJobOutcome> Process(
            AssetProcessingJobContext<ArchiveInspectionPayload> context,
            CancellationToken cancellationToken)
        {
            // Returns a MalwareScanResult instead of ArchiveInspectionResult
            return Task.FromResult(AssetProcessingJobOutcome.Succeeded(new MalwareScanResult(true)));
        }
    }

    private sealed class NullOutcomeHandler : IAssetProcessingJobHandler<ArchiveInspectionPayload, ArchiveInspectionResult>
    {
        public Task<AssetProcessingJobOutcome> Process(
            AssetProcessingJobContext<ArchiveInspectionPayload> context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<AssetProcessingJobOutcome>(null!);
        }
    }

    [Fact]
    public void Constructor_WhenDuplicateHandlersForSameType_ShouldThrowInvalidOperationException()
    {
        var adapter1 = new AssetProcessingJobHandlerAdapter<TestInspectionHandler, ArchiveInspectionPayload, ArchiveInspectionResult>(
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            new TestInspectionHandler());
        var adapter2 = new AssetProcessingJobHandlerAdapter<TestInspectionHandler, ArchiveInspectionPayload, ArchiveInspectionResult>(
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            new TestInspectionHandler());

        Func<AssetProcessingJobRegistry> act = () => new AssetProcessingJobRegistry([adapter1, adapter2]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate job handlers registered*");
    }

    [Fact]
    public void GetHandler_WhenMissing_ShouldReturnNull()
    {
        var registry = new AssetProcessingJobRegistry([]);

        registry.GetHandler(AssetProcessingJobType.MALWARE_SCAN).Should().BeNull();
        registry.HasHandler(AssetProcessingJobType.MALWARE_SCAN).Should().BeFalse();
    }

    [Fact]
    public async Task Adapter_Execute_WhenPayloadValid_ExecutesTypedHandler()
    {
        var adapter = new AssetProcessingJobHandlerAdapter<TestInspectionHandler, ArchiveInspectionPayload, ArchiveInspectionResult>(
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            new TestInspectionHandler());

        var payloadJson = AssetProcessingSerializer.SerializePayload(
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            new ArchiveInspectionPayload());

        var claimedJob = new ClaimedAssetProcessingJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            payloadJson,
            null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        AssetProcessingJobOutcome outcome = await adapter.Execute(claimedJob, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.Success>();
        var success = (AssetProcessingJobOutcome.Success)outcome;
        success.Result.Should().BeOfType<ArchiveInspectionResult>();
        ((ArchiveInspectionResult)success.Result).FileCount.Should().Be(10);
    }

    [Fact]
    public async Task Adapter_Execute_WhenResultTypeMismatch_ShouldThrowInvalidAssetProcessingJobResultException()
    {
        var adapter = new AssetProcessingJobHandlerAdapter<WrongResultHandler, ArchiveInspectionPayload, ArchiveInspectionResult>(
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            new WrongResultHandler());

        var payloadJson = AssetProcessingSerializer.SerializePayload(
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            new ArchiveInspectionPayload());

        var claimedJob = new ClaimedAssetProcessingJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            payloadJson,
            null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Func<Task<AssetProcessingJobOutcome>> act = () => adapter.Execute(claimedJob, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidAssetProcessingJobResultException>()
            .WithMessage("*returned result of type MalwareScanResult instead of ArchiveInspectionResult*");
    }

    [Fact]
    public async Task Adapter_Execute_WhenHandlerReturnsNullOutcome_ShouldThrowInvalidAssetProcessingJobResultException()
    {
        var adapter = new AssetProcessingJobHandlerAdapter<NullOutcomeHandler, ArchiveInspectionPayload, ArchiveInspectionResult>(
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            new NullOutcomeHandler());

        var payloadJson = AssetProcessingSerializer.SerializePayload(
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            new ArchiveInspectionPayload());

        var claimedJob = new ClaimedAssetProcessingJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            payloadJson,
            null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Func<Task<AssetProcessingJobOutcome>> act = () => adapter.Execute(claimedJob, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidAssetProcessingJobResultException>()
            .WithMessage("*returned a null outcome*");
    }

    [Fact]
    public void OutcomeFactories_ValidateArgumentsStrictly()
    {
        // Null result
        Func<AssetProcessingJobOutcome> actNullSuccess = () => AssetProcessingJobOutcome.Succeeded(null!);
        actNullSuccess.Should().Throw<ArgumentNullException>();

        // Bad error code format
        Func<AssetProcessingJobOutcome> actBadCode = () => AssetProcessingJobOutcome.Terminal("invalid-lowercase", "Safe summary");
        actBadCode.Should().Throw<ArgumentException>();

        // Empty summary
        Func<AssetProcessingJobOutcome> actEmptySummary = () => AssetProcessingJobOutcome.Terminal("VALID_CODE", "   ");
        actEmptySummary.Should().Throw<ArgumentException>();

        // Negative retry delay
        Func<AssetProcessingJobOutcome> actNegativeDelay = () => AssetProcessingJobOutcome.Retryable("VALID_CODE", "Safe summary", TimeSpan.FromSeconds(-5));
        actNegativeDelay.Should().Throw<ArgumentOutOfRangeException>();

        // Valid outcomes pass
        var validSuccess = AssetProcessingJobOutcome.Succeeded(new MalwareScanResult(true));
        validSuccess.Should().BeOfType<AssetProcessingJobOutcome.Success>();

        var validRetry = AssetProcessingJobOutcome.Retryable("TIMEOUT", "Scan timed out", TimeSpan.FromSeconds(30));
        validRetry.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();

        var validTerminal = AssetProcessingJobOutcome.Terminal("FATAL_ERROR", "Scanner fatal error");
        validTerminal.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();
    }

    [Fact]
    public void OutcomeTypes_AreImmutableClassesWithoutPublicSettersOrInit()
    {
        Type[] types =
        [
            typeof(AssetProcessingJobOutcome.Success),
            typeof(AssetProcessingJobOutcome.RetryableFailure),
            typeof(AssetProcessingJobOutcome.TerminalFailure)
        ];

        foreach (Type type in types)
        {
            type.IsClass.Should().BeTrue();

            // All public constructors should be 0 (only internal constructors)
            ConstructorInfo[] publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            publicConstructors.Should().BeEmpty($"{type.Name} should not have public constructors.");

            // All properties must have no public setter or init setter
            PropertyInfo[] publicProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo prop in publicProperties)
            {
                prop.CanWrite.Should().BeFalse($"{type.Name}.{prop.Name} should be get-only without setters or init.");
                prop.GetSetMethod(nonPublic: true).Should().BeNull();
            }
        }
    }
}
