using System.Reflection;
using System.Text.Json;
using AssetBlock.WebApi.Controllers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class ModerationDecisionInputTests
{
    private const string VALID_BASIS = """
        "basis": {"versionId": "123e4567-e89b-12d3-a456-426614174001", "contentSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "analysisReportId": "123e4567-e89b-12d3-a456-426614174002", "declarationRevision": 1, "materialMetadataRevision": 1,
        "policyVersion": "policy", "sellerEvidenceRevision": 0, "caseRevision": 1}
        """;

    [Fact]
    public void Decide_ShouldDeclareOneMebibyteBodyLimit()
    {
        MethodInfo decide = typeof(ModerationSubmissionsController).GetMethod(nameof(ModerationSubmissionsController.Decide))!;
        ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)decide.GetCustomAttribute<RequestSizeLimitAttribute>()!).MaxRequestBodySize.Should().Be(1_048_576);
    }

    [Fact]
    public void DecisionInput_WhenStringMembersAreComplete_ShouldDeserialize()
    {
        ModerationSubmissionsController.DecisionInput input = Deserialize("""
            {"operationId": "123e4567-e89b-12d3-a456-426614174003", 
            """ + VALID_BASIS + """

            , "outcome": "APPROVE", "rationale": "Reviewed.", "dispositions": [{"findingId": "123e4567-e89b-12d3-a456-426614174004", "disposition": "EXPLAINED", "rationale": "Explained."}]}
            """);
        input.Outcome.Should().Be(AssetBlock.Domain.Core.Enums.ModerationDecisionOutcome.APPROVE);
        input.Dispositions.Should().ContainSingle().Which.Disposition.Should().Be(AssetBlock.Domain.Core.Enums.FindingDispositionType.EXPLAINED);
    }

    [Theory]
    [InlineData("""{"operationId": "123e4567-e89b-12d3-a456-426614174003", """ + "\"outcome_missing\": 1, ")]
    [InlineData("""{"operationId": "123e4567-e89b-12d3-a456-426614174003", "outcome": 0, """)]
    [InlineData("""{"operationId": "123e4567-e89b-12d3-a456-426614174003", "outcome": 1, """)]
    [InlineData("""{"operationId": "123e4567-e89b-12d3-a456-426614174003", "outcome": "APPROVE_ALL", """)]
    public void DecisionInput_WhenOutcomeIsAbsentNumericOrUnknown_ShouldFailToDeserialize(string prefix)
    {
        Action act = () => Deserialize(prefix + VALID_BASIS + """
            , "rationale": "Reviewed.", "dispositions": []}
            """);
        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("""{"findingId": "123e4567-e89b-12d3-a456-426614174004", "rationale": "Explained."}""")]
    [InlineData("""{"findingId": "123e4567-e89b-12d3-a456-426614174004", "disposition": 0, "rationale": "Explained."}""")]
    [InlineData("""{"findingId": "123e4567-e89b-12d3-a456-426614174004", "disposition": "UNKNOWN", "rationale": "Explained."}""")]
    public void DecisionInput_WhenDispositionIsAbsentOrNumeric_ShouldFailToDeserialize(string disposition)
    {
        Action act = () => Deserialize("""
            {"operationId": "123e4567-e89b-12d3-a456-426614174003", 
            """ + VALID_BASIS + """

            , "outcome": "APPROVE", "rationale": "Reviewed.", "dispositions": [
            """ + disposition + "]}");
        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("null", "\"EXPLAINED\"", "Outcome")]
    [InlineData("\"APPROVE\"", "null", "Dispositions[0].Disposition")]
    public void DecisionValidator_WhenEnumIsNull_ShouldRejectOnlyThatField(string outcome, string disposition, string expectedField)
    {
        var json = "{\"operationId\":\"123e4567-e89b-12d3-a456-426614174003\"," + VALID_BASIS +
            ",\"outcome\":" + outcome + ",\"rationale\":\"Reviewed.\",\"dispositions\":[{\"findingId\":\"123e4567-e89b-12d3-a456-426614174004\",\"disposition\":" + disposition + ",\"rationale\":\"Reviewed.\"}]}";
        ModerationSubmissionsController.DecisionInput input = Deserialize(json);
        FluentValidation.Results.ValidationResult result = new ModerationSubmissionsController.DecisionInputValidator().Validate(input);
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be(expectedField);
    }

    private static ModerationSubmissionsController.DecisionInput Deserialize(string json) =>
        JsonSerializer.Deserialize<ModerationSubmissionsController.DecisionInput>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
