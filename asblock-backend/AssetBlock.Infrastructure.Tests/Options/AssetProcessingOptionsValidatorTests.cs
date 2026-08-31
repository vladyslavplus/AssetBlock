using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.Options;

public sealed class AssetProcessingOptionsValidatorTests
{
    private readonly AssetProcessingOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenOptionsAreValid_ShouldReturnSuccess()
    {
        var options = new AssetProcessingOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromSeconds(10),
            BatchSize = 10,
            Concurrency = 10,
            LeaseDuration = TimeSpan.FromMinutes(5),
            OperationTimeout = TimeSpan.FromMinutes(4),
            MaxAttempts = 3
        };

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenPollIntervalIsZeroOrNegative_ShouldReturnFailure(int seconds)
    {
        var options = new AssetProcessingOptions { PollInterval = TimeSpan.FromSeconds(seconds) };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("PollInterval must be between 1 second and 5 minutes"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenBatchSizeIsZeroOrNegative_ShouldReturnFailure(int value)
    {
        var options = new AssetProcessingOptions { BatchSize = value };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("BatchSize must be between 1 and 100"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Validate_WhenConcurrencyIsZeroOrNegative_ShouldReturnFailure(int value)
    {
        var options = new AssetProcessingOptions { Concurrency = value };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("Concurrency must be between 1 and 200"));
    }

    [Fact]
    public void Validate_WhenConcurrencyGreaterThanBatchSize_ShouldReturnFailure()
    {
        var options = new AssetProcessingOptions { Concurrency = 20, BatchSize = 10 };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("Concurrency cannot be greater than BatchSize"));
    }

    [Fact]
    public void Validate_WhenLeaseDurationDoesNotHaveSafetyMarginOverOperationTimeout_ShouldReturnFailure()
    {
        var options = new AssetProcessingOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(5),
            OperationTimeout = TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(45)
        };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("LeaseDuration must be at least 30 seconds greater than OperationTimeout"));
    }

    [Fact]
    public void Validate_WhenMaxAttemptsIsZero_ShouldReturnFailure()
    {
        var options = new AssetProcessingOptions { MaxAttempts = 0 };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("MaxAttempts must be between 1 and 10"));
    }

    [Fact]
    public void Validate_WhenInitialRetryDelayIsInvalid_ShouldReturnFailure()
    {
        var options = new AssetProcessingOptions { InitialRetryDelay = TimeSpan.FromSeconds(1) };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("InitialRetryDelay must be between 5 seconds and 10 minutes"));
    }

    [Fact]
    public void Validate_WhenMaxRetryDelayIsInvalid_ShouldReturnFailure()
    {
        var options = new AssetProcessingOptions { MaxRetryDelay = TimeSpan.FromSeconds(1) };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("MaxRetryDelay must be between 1 minute and 24 hours"));
    }

    [Fact]
    public void Validate_WhenInitialRetryDelayGreaterThanMaxRetryDelay_ShouldReturnFailure()
    {
        var options = new AssetProcessingOptions
        {
            InitialRetryDelay = TimeSpan.FromMinutes(10),
            MaxRetryDelay = TimeSpan.FromMinutes(5)
        };
        ValidateOptionsResult result = _sut.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("InitialRetryDelay cannot be greater than MaxRetryDelay"));
    }
}
