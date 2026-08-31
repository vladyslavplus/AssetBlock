using AssetBlock.Domain.Core.Dto;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class ArchiveAnalysisSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_ShouldRoundTripValidMetadata()
    {
        var manifest = new RecognizedManifestItem(
            FileName: "package.json",
            ManifestType: "npm",
            PackageName: "@assetblock/core",
            PackageVersion: "1.0.0",
            Description: "Core library with Ukrainian characters: Привіт Світ 🚀",
            Dependencies: ["react", "zod", "clsx"]
        );

        var metadata = new ArchiveAnalysisManifestMetadata([manifest]);

        var json = ArchiveAnalysisSerializer.SerializeManifestMetadata(metadata);
        json.Should().NotBeNullOrWhiteSpace();

        ArchiveAnalysisManifestMetadata restored = ArchiveAnalysisSerializer.DeserializeManifestMetadata(json);
        restored.Should().NotBeNull();
        restored.Manifests.Should().HaveCount(1);
        restored.Manifests[0].FileName.Should().Be("package.json");
        restored.Manifests[0].ManifestType.Should().Be("npm");
        restored.Manifests[0].PackageName.Should().Be("@assetblock/core");
        restored.Manifests[0].Description.Should().Contain("Привіт Світ 🚀");
        restored.Manifests[0].Dependencies.Should().BeEquivalentTo(["react", "zod", "clsx"]);
    }

    [Fact]
    public void Serialize_ShouldRejectPolymorphicType()
    {
        const string jsonWithPolymorphism = """{"$type":"evil","manifests":[]}""";
        Func<ArchiveAnalysisManifestMetadata> act = () => ArchiveAnalysisSerializer.DeserializeManifestMetadata(jsonWithPolymorphism);
        act.Should().Throw<ArchiveAnalysisSerializerException>()
            .WithMessage("*$type*");
    }

    [Fact]
    public void Serialize_ShouldRejectOversizedPayload()
    {
        // 17000 bytes string in description
        var largeString = new string('A', 17000);
        var manifest = new RecognizedManifestItem(
            FileName: "package.json",
            ManifestType: "npm",
            Description: largeString
        );

        var metadata = new ArchiveAnalysisManifestMetadata([manifest]);
        Func<string> act = () => ArchiveAnalysisSerializer.SerializeManifestMetadata(metadata);
        act.Should().Throw<ArchiveAnalysisSerializerException>();
    }

    [Fact]
    public void Serialize_ShouldRejectExcessiveManifestCount()
    {
        var list = Enumerable.Range(0, 9)
            .Select(i => new RecognizedManifestItem($"package{i}.json", "npm"))
            .ToList();

        var metadata = new ArchiveAnalysisManifestMetadata(list);
        Func<string> act = () => ArchiveAnalysisSerializer.SerializeManifestMetadata(metadata);
        act.Should().Throw<ArchiveAnalysisSerializerException>()
            .WithMessage("*exceeds maximum of 8*");
    }

    [Fact]
    public void Serialize_ShouldRejectEmptyFileName()
    {
        var manifest = new RecognizedManifestItem("   ", "npm");
        var metadata = new ArchiveAnalysisManifestMetadata([manifest]);
        Func<string> act = () => ArchiveAnalysisSerializer.SerializeManifestMetadata(metadata);
        act.Should().Throw<ArchiveAnalysisSerializerException>()
            .WithMessage("*FileName*");
    }

    [Fact]
    public void Deserialize_ShouldRejectNonObjectOrNull()
    {
        Func<ArchiveAnalysisManifestMetadata> nullAct = () => ArchiveAnalysisSerializer.DeserializeManifestMetadata("   ");
        nullAct.Should().Throw<ArchiveAnalysisSerializerException>();

        Func<ArchiveAnalysisManifestMetadata> nonObjectAct = () => ArchiveAnalysisSerializer.DeserializeManifestMetadata("[\"not an object\"]");
        nonObjectAct.Should().Throw<ArchiveAnalysisSerializerException>();
    }
}
