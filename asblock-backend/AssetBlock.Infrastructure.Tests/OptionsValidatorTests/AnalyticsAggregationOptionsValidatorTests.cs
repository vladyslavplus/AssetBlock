using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class AnalyticsAggregationOptionsValidatorTests
{
    private readonly AnalyticsAggregationOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenDefaults_ShouldSucceed()
    {
        _sut.Validate(null, new AnalyticsAggregationOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(29)]
    [InlineData(3601)]
    public void Validate_WhenIntervalSecondsOutOfRange_ShouldFail(int intervalSeconds)
    {
        var result = _sut.Validate(null, new AnalyticsAggregationOptions { IntervalSeconds = intervalSeconds });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("IntervalSeconds");
    }

    [Theory]
    [InlineData(99)]
    [InlineData(50_001)]
    public void Validate_WhenRetentionBatchSizeOutOfRange_ShouldFail(int batchSize)
    {
        var result = _sut.Validate(null, new AnalyticsAggregationOptions { RetentionBatchSize = batchSize });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("RetentionBatchSize");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_WhenMaxRetentionBatchesOutOfRange_ShouldFail(int maxBatches)
    {
        var result = _sut.Validate(null, new AnalyticsAggregationOptions { MaxRetentionBatchesPerRun = maxBatches });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("MaxRetentionBatchesPerRun");
    }

    [Theory]
    [InlineData(9)]
    [InlineData(601)]
    public void Validate_WhenCommandTimeoutOutOfRange_ShouldFail(int timeoutSeconds)
    {
        var result = _sut.Validate(null, new AnalyticsAggregationOptions { CommandTimeoutSeconds = timeoutSeconds });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CommandTimeoutSeconds");
    }
}
