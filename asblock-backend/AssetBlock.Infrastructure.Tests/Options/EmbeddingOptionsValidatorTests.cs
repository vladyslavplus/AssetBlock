using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.Options;

public sealed class EmbeddingOptionsValidatorTests
{
    private const string VALID_DIGEST = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Validate_WhenDisabledWithEmptyModelAndZeroDimension_ShouldSucceed()
    {
        var validator = new EmbeddingOptionsValidator();
        var options = new EmbeddingOptions
        {
            Enabled = false,
            Provider = "Ollama",
            BaseUrl = "http://localhost:11434",
            Model = "",
            Revision = "",
            Digest = "",
            Dimension = 0
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledWithDefaultEmptyFields_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        var options = new EmbeddingOptions
        {
            Enabled = true,
            Provider = "Ollama",
            BaseUrl = "http://localhost:11434",
            Model = "",
            Revision = "",
            Digest = "",
            Dimension = 0
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Model");
        result.FailureMessage.Should().Contain("Revision");
        result.FailureMessage.Should().Contain("Digest");
        result.FailureMessage.Should().Contain("Dimension");
    }

    [Fact]
    public void Validate_WhenEnabledWithNonOllamaProvider_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.Provider = "OpenRouter";

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Provider");
    }

    [Fact]
    public void Validate_WhenEnabledWithNonLoopbackUrl_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.BaseUrl = "http://remote-ollama.example.com:11434";

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("loopback");
    }

    [Fact]
    public void Validate_WhenEnabledWithInvalidDigest_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.Digest = "not-a-valid-sha256";

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Digest");
    }

    [Fact]
    public void Validate_WhenEnabledWithInvalidSchemaVersion_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.ContentSchemaVersion = "wrong-schema-v2";

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ContentSchemaVersion");
    }

    [Fact]
    public void Validate_WhenEnabledWithFloatingLatestTag_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.Model = "bge-m3:latest";

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("non-floating");
    }

    [Fact]
    public void Validate_WhenEnabledWithUntaggedModel_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.Model = "bge-m3";

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("non-floating");
    }

    [Fact]
    public void Validate_WhenEnabledWithRequestTimeoutExceedsMax_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.RequestTimeoutSeconds = EmbeddingOptions.MAX_REQUEST_TIMEOUT_SECONDS + 1;

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("RequestTimeoutSeconds");
    }

    [Fact]
    public void Validate_WhenEnabledWithQueryTimeoutExceedsMax_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.QueryTimeoutMilliseconds = EmbeddingOptions.MAX_QUERY_TIMEOUT_MILLISECONDS + 1;

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("QueryTimeoutMilliseconds");
    }

    [Fact]
    public void Validate_WhenEnabledWithMaxInputCharsExceedsMax_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.MaxInputChars = EmbeddingOptions.MAX_INPUT_CHARS + 1;

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("MaxInputChars");
    }

    [Fact]
    public void Validate_WhenEnabledWithBackfillBatchSizeExceedsMax_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.BackfillBatchSize = EmbeddingOptions.MAX_BACKFILL_BATCH_SIZE + 1;

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("BackfillBatchSize");
    }

    [Fact]
    public void Validate_WhenEnabledWithBackfillPollSecondsExceedsMax_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.BackfillPollSeconds = EmbeddingOptions.MAX_BACKFILL_POLL_SECONDS + 1;

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("BackfillPollSeconds");
    }

    [Fact]
    public void Validate_WhenEnabledWithDimensionExceedsMax_ShouldFail()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();
        options.Dimension = EmbeddingOptions.MAX_DIMENSION + 1;

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Dimension");
    }

    [Fact]
    public void Validate_WhenEnabledWithValidFields_ShouldSucceed()
    {
        var validator = new EmbeddingOptionsValidator();
        EmbeddingOptions options = ValidEnabledOptions();

        ValidateOptionsResult result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    private static EmbeddingOptions ValidEnabledOptions() => new()
    {
        Enabled = true,
        Provider = "Ollama",
        BaseUrl = "http://127.0.0.1:11434",
        Model = "bge-m3:q8_0",
        Revision = "rev-1",
        Digest = VALID_DIGEST,
        Dimension = 1024,
        ContentSchemaVersion = "asset-public-metadata-v1",
        RequestTimeoutSeconds = 10,
        QueryTimeoutMilliseconds = 900,
        MaxInputChars = 8192,
        BackfillBatchSize = 50,
        BackfillPollSeconds = 30
    };
}
