using System.Text.Json;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public class AssetProcessingSerializerTests
{
    private const string VALID_SHA256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void RoundTrip_WithArchiveInspectionPayload_ShouldPreserveValue()
    {
        var payload = new ArchiveInspectionPayload();

        var json = AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.ARCHIVE_INSPECTION, payload);
        ArchiveInspectionPayload restored = Assert.IsType<ArchiveInspectionPayload>(AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.ARCHIVE_INSPECTION, json));

        restored.Should().Be(payload);
    }

    [Theory]
    [InlineData("v1.0")]
    [InlineData("2026-08-24")]
    public void RoundTrip_WithMalwareScanPayload_ShouldPreservePolicyVersion(string policyVersion)
    {
        var payload = new MalwareScanPayload(policyVersion);

        var json = AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.MALWARE_SCAN, payload);
        MalwareScanPayload restored = Assert.IsType<MalwareScanPayload>(AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.MALWARE_SCAN, json));

        restored.PolicyVersion.Should().Be(policyVersion);
    }

    [Fact]
    public void RoundTrip_WithListingCopilotPayload_ShouldPreservePolicyVersion()
    {
        var payload = new ListingCopilotPayload("policy-v2");

        var json = AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.LISTING_COPILOT, payload);
        ListingCopilotPayload restored = Assert.IsType<ListingCopilotPayload>(AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.LISTING_COPILOT, json));

        restored.PolicyVersion.Should().Be("policy-v2");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 3456789)]
    public void RoundTrip_WithArchiveInspectionResult_ShouldPreserveCounts(int fileCount, long totalSize)
    {
        var result = new ArchiveInspectionResult(fileCount, totalSize);

        var json = AssetProcessingSerializer.SerializeResult(AssetProcessingJobType.ARCHIVE_INSPECTION, result);
        ArchiveInspectionResult restored = Assert.IsType<ArchiveInspectionResult>(AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.ARCHIVE_INSPECTION, json));

        restored.FileCount.Should().Be(fileCount);
        restored.TotalSizeUncompressed.Should().Be(totalSize);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_WithMalwareScanResult_ShouldPreserveOutcome(bool isClean)
    {
        var result = new MalwareScanResult(isClean);

        var json = AssetProcessingSerializer.SerializeResult(AssetProcessingJobType.MALWARE_SCAN, result);
        MalwareScanResult restored = Assert.IsType<MalwareScanResult>(AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.MALWARE_SCAN, json));

        restored.IsClean.Should().Be(isClean);
    }

    [Fact]
    public void RoundTrip_WithListingCopilotResult_ShouldPreserveValues()
    {
        var result = new ListingCopilotResult(true, VALID_SHA256);

        var json = AssetProcessingSerializer.SerializeResult(AssetProcessingJobType.LISTING_COPILOT, result);
        ListingCopilotResult restored = Assert.IsType<ListingCopilotResult>(AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.LISTING_COPILOT, json));

        restored.Success.Should().BeTrue();
        restored.ContentHash.Should().Be(VALID_SHA256);
    }

    // ---------- Serialize: allowlist mapping and semantic validation ----------

    [Fact]
    public void SerializePayload_WithWrongDtoForType_ThrowsControlledException()
    {
        Func<string> act = () => AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.MALWARE_SCAN, new ArchiveInspectionPayload());
        act.Should().Throw<AssetProcessingSerializerException>().WithMessage("*Expected MalwareScanPayload*").And.InnerException.Should().BeNull();
    }

    [Fact]
    public void SerializeResult_WithWrongDtoForType_ThrowsControlledException()
    {
        Func<string> act = () => AssetProcessingSerializer.SerializeResult(AssetProcessingJobType.ARCHIVE_INSPECTION, new MalwareScanResult(true));
        act.Should().Throw<AssetProcessingSerializerException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("this_is_a_very_long_policy_version_string_that_exceeds_the_limit_of_64_characters")]
    public void SerializeMalwareScanPayload_WithInvalidPolicyVersion_FailsClosed(string? policy)
    {
        var payload = new MalwareScanPayload(policy!);
        Func<string> act = () => AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.MALWARE_SCAN, payload);
        act.Should().Throw<AssetProcessingSerializerException>().WithMessage("*PolicyVersion*");
    }

    [Fact]
    public void Deserialize_WhenPolymorphicTypeMetadataPresent_ThrowsControlledException()
    {
        const string json = """{"$type":"AssetBlock.Domain.Core.Dto.MalwareScanPayload, AssetBlock.Domain","policyVersion":"v1"}""";

        Func<AssetProcessingPayload> payloadAct = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.MALWARE_SCAN, json);
        payloadAct.Should().Throw<AssetProcessingSerializerException>().WithMessage("*Polymorphic type metadata*");

        Func<AssetProcessingResult> resultAct = () => AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.MALWARE_SCAN, json);
        resultAct.Should().Throw<AssetProcessingSerializerException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Deserialize_WhenEmptyOrWhitespace_ThrowsControlledException(string json)
    {
        Func<AssetProcessingPayload> act = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.MALWARE_SCAN, json);
        act.Should().Throw<AssetProcessingSerializerException>().WithMessage("*non-empty*");
    }

    [Fact]
    public void Deserialize_WhenOversizeInput_ThrowsControlledExceptionBeforeParsing()
    {
        var json = "{\"policyVersion\":\"" + new string('v', 5000) + "\"}";
        Func<AssetProcessingPayload> act = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.MALWARE_SCAN, json);
        act.Should().Throw<AssetProcessingSerializerException>().WithMessage("*exceeds 4000 bytes*");
    }

    [Fact]
    public void Deserialize_WhenJsonNullLiteral_ThrowsControlledException()
    {
        Func<AssetProcessingPayload> payloadAct = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.MALWARE_SCAN, "null");
        payloadAct.Should().Throw<AssetProcessingSerializerException>().WithMessage("*must not be null*");

        Func<AssetProcessingResult> resultAct = () => AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.ARCHIVE_INSPECTION, "null");
        resultAct.Should().Throw<AssetProcessingSerializerException>();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("{invalid")]
    [InlineData("{}{}")]
    public void Deserialize_WhenWrongShapeOrMalformed_ThrowsControlledException(string json)
    {
        Func<AssetProcessingPayload> payloadAct = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.LISTING_COPILOT, json);
        payloadAct.Should().Throw<AssetProcessingSerializerException>();

        Func<AssetProcessingResult> resultAct = () => AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.LISTING_COPILOT, json);
        resultAct.Should().Throw<AssetProcessingSerializerException>();
    }

    [Fact]
    public void Deserialize_WhenUnknownFieldPresent_ThrowsControlledException()
    {
        const string json = """{"policyVersion":"v1","unexpected":true}""";

        Func<AssetProcessingPayload> act = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.MALWARE_SCAN, json);
        act.Should().Throw<AssetProcessingSerializerException>().WithMessage("*unknown fields*");
    }

    [Fact]
    public void Deserialize_WhenRequiredFieldMissing_ThrowsControlledException()
    {
        const string json = "{}";

        Func<AssetProcessingPayload> payloadAct = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.MALWARE_SCAN, json);
        payloadAct.Should().Throw<AssetProcessingSerializerException>().WithMessage("*PolicyVersion*");

        Func<AssetProcessingResult> resultAct = () => AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.LISTING_COPILOT, json);
        resultAct.Should().Throw<AssetProcessingSerializerException>().WithMessage("*ContentHash*");
    }

    [Fact]
    public void Deserialize_WhenNullFieldValueForNonNullableProperty_ThrowsControlledException()
    {
        const string json = """{"policyVersion":null}""";
        Func<AssetProcessingPayload> act = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.MALWARE_SCAN, json);
        act.Should().Throw<AssetProcessingSerializerException>();
    }

    [Fact]
    public void Deserialize_WhenNumericOverflow_ThrowsControlledException()
    {
        const string json = """{"fileCount":99999999999999999999,"totalSizeUncompressed":0}""";
        Func<AssetProcessingResult> act = () => AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.ARCHIVE_INSPECTION, json);
        act.Should().Throw<AssetProcessingSerializerException>();
    }

    [Fact]
    public void Deserialize_WhenDeeplyNestedJson_ThrowsControlledException()
    {
        const string json = """{"a":{"a":{"a":{"a":{"a":{"a":1}}}}}}""";
        Func<AssetProcessingPayload> act = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.ARCHIVE_INSPECTION, json);
        act.Should().Throw<AssetProcessingSerializerException>();
    }

    [Fact]
    public void Deserialize_WhenArchiveResultHasNegativeFileCount_ThrowsControlledException()
    {
        const string json = """{"fileCount":-1,"totalSizeUncompressed":0}""";
        Func<AssetProcessingResult> act = () => AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.ARCHIVE_INSPECTION, json);
        act.Should().Throw<AssetProcessingSerializerException>().WithMessage("*negative*");
    }

    [Fact]
    public void Deserialize_WhenListingCopilotResultHasInvalidHash_ThrowsControlledException()
    {
        const string json = """{"success":true,"contentHash":"not-a-hash"}""";
        Func<AssetProcessingResult> act = () => AssetProcessingSerializer.DeserializeResult(AssetProcessingJobType.LISTING_COPILOT, json);
        act.Should().Throw<AssetProcessingSerializerException>().WithMessage("*SHA-256*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("this_is_a_very_long_policy_version_string_that_exceeds_the_limit_of_64_characters_x")]
    public void Deserialize_WhenPayloadPolicyVersionInvalid_ThrowsControlledException(string policyVersion)
    {
        var json = JsonSerializer.Serialize(
            new { policyVersion },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Func<AssetProcessingPayload> act = () => AssetProcessingSerializer.DeserializePayload(AssetProcessingJobType.LISTING_COPILOT, json);
        act.Should().Throw<AssetProcessingSerializerException>().WithMessage("*PolicyVersion*");
    }
}
