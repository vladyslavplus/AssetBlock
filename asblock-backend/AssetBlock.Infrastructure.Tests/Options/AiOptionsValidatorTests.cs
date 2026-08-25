using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using AssetBlock.Infrastructure.Tests.Ai;
using Microsoft.Extensions.Configuration;

namespace AssetBlock.Infrastructure.Tests.Options;

public sealed class AiOptionsValidatorTests
{
    [Fact]
    public void Validate_WhenDisabled_ShouldAllowUnknownProviderAndEmptySecrets()
    {
        var result = new AiOptionsValidator().Validate(null, new AiOptions
        {
            Enabled = false,
            Provider = "NotAProvider",
            PromptPolicyVersion = "listing-copilot-v1",
            ModelPolicyPath = "ai/model-policy.json"
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledWithUnknownProvider_ShouldFail()
    {
        var result = new AiOptionsValidator().Validate(null, new AiOptions
        {
            Enabled = true,
            Provider = "SemanticKernel",
            PromptPolicyVersion = "listing-copilot-v1",
            ModelPolicyPath = "ai/model-policy.json"
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Provider");
    }

    [Fact]
    public void Validate_WhenEnabledWithKnownProvider_ShouldSucceed()
    {
        var result = new AiOptionsValidator().Validate(null, new AiOptions
        {
            Enabled = true,
            Provider = "OpenRouter",
            PromptPolicyVersion = "listing-copilot-v1",
            ModelPolicyPath = "ai/model-policy.json"
        });

        result.Succeeded.Should().BeTrue();
    }
}

public sealed class OpenRouterAndOllamaOptionsValidatorTests
{
    [Fact]
    public void OpenRouter_WhenAiDisabled_ShouldSkipApiKeyAndModels()
    {
        var config = DisabledConfig();
        var sut = new OpenRouterOptionsValidator(config, new StaticAiModelPolicyCatalog());

        var result = sut.Validate(null, new OpenRouterOptions { ApiKey = "", Models = [] });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OpenRouter_WhenOllamaIsActive_ShouldSkipOpenRouterSecrets()
    {
        var config = EnabledConfig("Ollama");
        var sut = new OpenRouterOptionsValidator(config, new StaticAiModelPolicyCatalog());

        var result = sut.Validate(null, new OpenRouterOptions { ApiKey = "", Models = [] });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OpenRouter_WhenActiveWithoutPolicyEntry_ShouldFail()
    {
        var config = EnabledConfig("OpenRouter");
        var sut = new OpenRouterOptionsValidator(config, new StaticAiModelPolicyCatalog());
        var options = ValidOpenRouterOptions();

        var result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("policy");
    }

    [Fact]
    public void OpenRouter_WhenActiveWithFixturePolicy_ShouldSucceed()
    {
        var config = EnabledConfig("OpenRouter");
        var sut = new OpenRouterOptionsValidator(config, new StaticAiModelPolicyCatalog(StaticAiModelPolicyCatalog.OpenRouterFixture()));

        var result = sut.Validate(null, ValidOpenRouterOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OpenRouter_WhenConfiguredLimitsExceedPolicy_ShouldFail()
    {
        var config = EnabledConfig("OpenRouter");
        var sut = new OpenRouterOptionsValidator(config, new StaticAiModelPolicyCatalog(StaticAiModelPolicyCatalog.OpenRouterFixture()));
        var options = ValidOpenRouterOptions();
        options.MaxInputChars = 12001;

        var result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("limits");
    }

    [Fact]
    public void OpenRouter_WhenModelsAreDuplicated_ShouldFail()
    {
        var config = EnabledConfig("OpenRouter");
        var sut = new OpenRouterOptionsValidator(config, new StaticAiModelPolicyCatalog(StaticAiModelPolicyCatalog.OpenRouterFixture()));
        var options = ValidOpenRouterOptions();
        options.Models = ["fixture/openrouter-test", "fixture/openrouter-test"];

        var result = sut.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Ollama_WhenActiveWithNonLoopbackUrl_ShouldFail()
    {
        var config = EnabledConfig("Ollama");
        var sut = new OllamaOptionsValidator(config, new StaticAiModelPolicyCatalog(StaticAiModelPolicyCatalog.OllamaFixture()));

        var result = sut.Validate(null, new OllamaOptions
        {
            BaseUrl = "http://ollama.example:11434",
            Model = "fixture-ollama-test",
            Timeout = TimeSpan.FromMinutes(2),
            MaxInputChars = 12000,
            MaxOutputTokens = 1000
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("loopback");
    }

    [Fact]
    public void Ollama_WhenActiveWithFixturePolicy_ShouldSucceed()
    {
        var config = EnabledConfig("Ollama");
        var sut = new OllamaOptionsValidator(config, new StaticAiModelPolicyCatalog(StaticAiModelPolicyCatalog.OllamaFixture()));

        var result = sut.Validate(null, new OllamaOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = "fixture-ollama-test",
            Timeout = TimeSpan.FromMinutes(2),
            MaxInputChars = 12000,
            MaxOutputTokens = 1000
        });

        result.Succeeded.Should().BeTrue();
    }

    private static OpenRouterOptions ValidOpenRouterOptions() => new()
    {
        BaseUrl = "https://openrouter.ai/api/v1",
        ApiKey = "sk-test-key-value",
        Models = ["fixture/openrouter-test"],
        Timeout = TimeSpan.FromMinutes(1),
        MaxInputChars = 12000,
        MaxOutputTokens = 1000,
        MaxRetryAfter = TimeSpan.FromHours(1),
        SiteUrl = "https://example.test",
        AppName = "AssetBlock"
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
