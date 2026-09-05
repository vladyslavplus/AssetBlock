using System.Text.RegularExpressions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed partial class EmbeddingModelKeyTests
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Hex64Regex();

    [Fact]
    public void Compute_WithFixedModelProvenance_ProducesValid64CharLowerHexKey()
    {
        var options = new EmbeddingOptions
        {
            Provider = "Ollama",
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Digest = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Dimension = 768,
            ContentSchemaVersion = "asset-public-metadata-v1"
        };

        var key = EmbeddingModelKey.Compute(options);

        key.Should().NotBeNullOrWhiteSpace();
        key.Length.Should().Be(64);
        Hex64Regex().IsMatch(key).Should().BeTrue();
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var key1 = EmbeddingModelKey.Compute("Ollama", "model-a", "rev-1", "sha256:abc", 768, "v1");
        var key2 = EmbeddingModelKey.Compute("Ollama", "model-a", "rev-1", "sha256:abc", 768, "v1");

        key1.Should().Be(key2);
    }

    [Theory]
    [InlineData("DifferentProvider", "model-a", "rev-1", "sha256:abc", 768, "v1")]
    [InlineData("Ollama", "different-model", "rev-1", "sha256:abc", 768, "v1")]
    [InlineData("Ollama", "model-a", "rev-2", "sha256:abc", 768, "v1")]
    [InlineData("Ollama", "model-a", "rev-1", "sha256:def", 768, "v1")]
    [InlineData("Ollama", "model-a", "rev-1", "sha256:abc", 512, "v1")]
    [InlineData("Ollama", "model-a", "rev-1", "sha256:abc", 768, "v2")]
    public void Compute_WhenAnyFieldChanges_ProducesDifferentKey(
        string provider,
        string model,
        string revision,
        string digest,
        int dimension,
        string contentSchemaVersion)
    {
        var baseline = EmbeddingModelKey.Compute("Ollama", "model-a", "rev-1", "sha256:abc", 768, "v1");
        var changed = EmbeddingModelKey.Compute(provider, model, revision, digest, dimension, contentSchemaVersion);

        changed.Should().NotBe(baseline);
    }

    [Fact]
    public void Compute_WithNullOptions_ThrowsArgumentNullException()
    {
        Action act = () => EmbeddingModelKey.Compute(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
