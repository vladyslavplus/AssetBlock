using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using AssetBlock.Infrastructure.Tests.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.Options;

public sealed class AiOptionsValidatorTests
{
    [Fact]
    public void Validate_WhenDisabled_ShouldAllowUnknownProviderAndEmptySecrets()
    {
        ValidateOptionsResult result = new AiOptionsValidator().Validate(null, new AiOptions
        {
            Enabled = false,
            Provider = "NotAProvider",
            PromptPolicyVersion = "listing-copilot-v1"
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledWithUnknownProvider_ShouldFail()
    {
        ValidateOptionsResult result = new AiOptionsValidator().Validate(null, new AiOptions
        {
            Enabled = true,
            Provider = "SemanticKernel",
            PromptPolicyVersion = "listing-copilot-v1"
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Provider");
    }

    [Fact]
    public void Validate_WhenEnabledWithKnownProvider_ShouldSucceed()
    {
        ValidateOptionsResult result = new AiOptionsValidator().Validate(null, new AiOptions
        {
            Enabled = true,
            Provider = "OpenRouter",
            PromptPolicyVersion = "listing-copilot-v1"
        });

        result.Succeeded.Should().BeTrue();
    }
}

public sealed class OpenRouterAndOllamaOptionsValidatorTests
{
    [Fact]
    public void OpenRouter_WhenAiDisabled_ShouldSkipApiKeyAndModels()
    {
        var sut = new OpenRouterOptionsValidator(DisabledConfig());

        ValidateOptionsResult result = sut.Validate(null, new OpenRouterOptions { ApiKey = "", Models = [] });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OpenRouter_WhenOllamaIsActive_ShouldSkipOpenRouterSecrets()
    {
        var sut = new OpenRouterOptionsValidator(EnabledConfig("Ollama"));

        ValidateOptionsResult result = sut.Validate(null, new OpenRouterOptions { ApiKey = "", Models = ["placeholder/not-validated"] });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OpenRouter_WhenActiveWithValidModels_ShouldSucceed()
    {
        var sut = new OpenRouterOptionsValidator(EnabledConfig("OpenRouter"));

        ValidateOptionsResult result = sut.Validate(null, ValidOpenRouterOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OpenRouter_WhenModelsAreEmpty_ShouldFail()
    {
        var sut = new OpenRouterOptionsValidator(EnabledConfig("OpenRouter"));
        OpenRouterOptions options = ValidOpenRouterOptions();
        options.Models = [];

        ValidateOptionsResult result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Models");
    }

    [Fact]
    public void OpenRouter_WhenModelsAreDuplicated_ShouldFail()
    {
        var sut = new OpenRouterOptionsValidator(EnabledConfig("OpenRouter"));
        OpenRouterOptions options = ValidOpenRouterOptions();
        options.Models = ["fixture/openrouter-test", "fixture/openrouter-test"];

        ValidateOptionsResult result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("distinct");
    }

    [Fact]
    public void OpenRouter_WhenModelIdIsOversized_ShouldFail()
    {
        var sut = new OpenRouterOptionsValidator(EnabledConfig("OpenRouter"));
        OpenRouterOptions options = ValidOpenRouterOptions();
        options.Models = [new string('a', 201)];

        ValidateOptionsResult result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("bounded");
    }

    [Fact]
    public void OpenRouter_WhenTooManyModels_ShouldFail()
    {
        var sut = new OpenRouterOptionsValidator(EnabledConfig("OpenRouter"));
        OpenRouterOptions options = ValidOpenRouterOptions();
        options.Models = Enumerable.Range(0, 17).Select(i => $"model-{i}").ToList();

        ValidateOptionsResult result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("16");
    }

    [Fact]
    public void Ollama_WhenOpenRouterIsActive_ShouldSkipOllamaPlaceholders()
    {
        var sut = new OllamaOptionsValidator(EnabledConfig("OpenRouter"));

        ValidateOptionsResult result = sut.Validate(null, new OllamaOptions
        {
            BaseUrl = "http://example.invalid:11434",
            Model = "",
            Digest = ""
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Ollama_WhenActiveWithNonLoopbackUrl_ShouldFail()
    {
        var sut = new OllamaOptionsValidator(EnabledConfig("Ollama"));

        OllamaOptions options = ValidOllamaOptions();
        options.BaseUrl = "http://ollama.example:11434";

        ValidateOptionsResult result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("loopback");
    }

    [Fact]
    public void Ollama_WhenActiveWithoutDigest_ShouldFail()
    {
        var sut = new OllamaOptionsValidator(EnabledConfig("Ollama"));

        OllamaOptions options = ValidOllamaOptions();
        options.Digest = "";

        ValidateOptionsResult result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Digest");
    }

    [Fact]
    public void Ollama_WhenActiveWithValidModelAndDigest_ShouldSucceed()
    {
        var sut = new OllamaOptionsValidator(EnabledConfig("Ollama"));

        ValidateOptionsResult result = sut.Validate(null, ValidOllamaOptions());

        result.Succeeded.Should().BeTrue();
    }

    private static OpenRouterOptions ValidOpenRouterOptions() => new()
    {
        BaseUrl = "https://openrouter.ai/api/v1",
        ApiKey = "sk-test-key-value",
        Models = ["fixture/openrouter-test", "fixture/openrouter-test-b"],
        Timeout = TimeSpan.FromMinutes(1),
        MaxInputChars = 12000,
        MaxOutputTokens = 1000,
        MaxRetryAfter = TimeSpan.FromHours(1),
        SiteUrl = "https://example.test",
        AppName = "AssetBlock"
    };

    private static OllamaOptions ValidOllamaOptions() => new()
    {
        BaseUrl = "http://127.0.0.1:11434",
        Model = "fixture-ollama-test",
        Digest = AiTestDigests.FixtureDigest,
        Timeout = TimeSpan.FromMinutes(2),
        MaxInputChars = 12000,
        MaxOutputTokens = 1000
    };

    private static IConfiguration DisabledConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "false",
            ["Ai:Provider"] = "OpenRouter"
        }).Build();

    private static IConfiguration EnabledConfig(string provider) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:Provider"] = provider
        }).Build();
}
