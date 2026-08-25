using AssetBlock.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace AssetBlock.Infrastructure.Tests.Ai;

public sealed class FileAiModelPolicyCatalogTests
{
    [Fact]
    public void Constructor_WhenAiDisabled_ShouldIgnoreMissingAndMalformedPolicy()
    {
        var malformed = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(malformed, """{"schemaVersion":1,"unexpected":true}""");
        var env = new TestHostEnvironment { ContentRootPath = Path.GetTempPath() };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "false",
            ["Ai:ModelPolicyPath"] = malformed
        }).Build();

        var catalog = new FileAiModelPolicyCatalog(env, config);

        catalog.SchemaVersion.Should().Be(1);
        catalog.TryGet(Domain.Core.Enums.AiProviderKind.OPENROUTER, "fixture/openrouter-test", out _).Should().BeFalse();
        File.Delete(malformed);
    }

    [Fact]
    public void Constructor_WhenAiEnabledAndPolicyHasUnknownProperty_ShouldThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "policyVersion": "listing-copilot-v1",
              "unexpected": true,
              "entries": []
            }
            """);
        var env = new TestHostEnvironment { ContentRootPath = Path.GetTempPath() };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ModelPolicyPath"] = path
        }).Build();

        var act = () => new FileAiModelPolicyCatalog(env, config);

        act.Should().Throw<JsonException>();
        File.Delete(path);
    }

    [Fact]
    public void Constructor_WhenAiEnabledAndFixturePolicyExists_ShouldLoadExactEntries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Ai", "model-policy.fixture.json");
        File.Exists(path).Should().BeTrue();
        var env = new TestHostEnvironment { ContentRootPath = AppContext.BaseDirectory };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ModelPolicyPath"] = path
        }).Build();

        var catalog = new FileAiModelPolicyCatalog(env, config);

        catalog.TryGet(Domain.Core.Enums.AiProviderKind.OPENROUTER, "fixture/openrouter-test", out var openRouter)
            .Should().BeTrue();
        openRouter!.StructuredOutput.Should().BeTrue();
        openRouter.Digest.Should().BeNull();
        catalog.TryGet(Domain.Core.Enums.AiProviderKind.OLLAMA, "fixture-ollama-test", out var ollama)
            .Should().BeTrue();
        ollama!.Privacy.Should().Be(Domain.Core.Enums.AiPrivacyDecision.LOCAL_ONLY);
        ollama.Digest.Should().Be(StaticAiModelPolicyCatalog.FixtureDigest);
        catalog.TryGet(Domain.Core.Enums.AiProviderKind.OPENROUTER, "openai/gpt-4o", out _).Should().BeFalse();
    }
}
