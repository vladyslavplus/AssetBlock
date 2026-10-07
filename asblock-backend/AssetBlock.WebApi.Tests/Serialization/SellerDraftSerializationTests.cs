using System.Text.Json;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;

namespace AssetBlock.WebApi.Tests.Serialization;

/// <summary>
/// HTTP contract tests for seller draft DTOs: exposed enums must serialize as canonical
/// UPPER_SNAKE_CASE strings and responses must deserialize symmetrically, mirroring the
/// Zod contracts the frontend parses.
/// </summary>
public class SellerDraftSerializationTests
{
    private static readonly Guid _assetId = Guid.NewGuid();
    private static readonly Guid _workspaceId = Guid.NewGuid();
    private static readonly Guid _versionId = Guid.NewGuid();

    private static readonly SourceDeclarationPayload _thirdPartyDeclaration = new(
        OwnContributionSummary: "Authored the pipeline.",
        OwnChanges: null,
        EarlierWork: null,
        RedistributionAcknowledged: true,
        DisclosurePolicyVersion: "1",
        Components:
        [
            new SourceDeclarationComponent(
                ComponentId: "c1",
                Name: "left-pad",
                PackagePathOrRange: "libs/left-pad@1.0.0",
                SourceUrl: "https://example.com/left-pad",
                KnownVersion: "1.0.0",
                License: "MIT",
                NoticeLocations: ["NOTICE.txt"],
                Modifications: null,
                PermissionEvidenceReferences: [],
                Origin: SourceComponentOrigin.THIRD_PARTY)
        ]);

    private static readonly JsonSerializerOptions _webJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Serialize_WhenDeclarationHasThirdPartyOrigin_ShouldEmitStringOriginProperty()
    {
        var dto = new SellerDraftSnapshotDto(
            _assetId, _workspaceId, 4, 1,
            new SellerDraftMaterialPayload("Title", null, Guid.NewGuid(), ["tools"]),
            _thirdPartyDeclaration,
            DeclarationComplete: true,
            LatestVersionId: null,
            LatestVersionNumber: null);

        var json = JsonSerializer.Serialize(dto, _webJson);

        json.Should().Contain("\"origin\":\"THIRD_PARTY\"");
        json.Should().Contain("\"components\":");
        json.Should().NotContain("\"ORIGIN_KIND\"");
    }

    [Fact]
    public void Roundtrip_WhenDeclarationSnapshotHasUnknownState_ShouldKeepStringEnums()
    {
        var dto = new SellerVersionReviewDto(
            _assetId, _versionId, 2,
            AssetVersionProcessingStatus.READY,
            ProcessingErrorCode: null,
            ProcessingErrorSummary: null,
            Analysis: SellerAnalysisAvailability.ANALYSIS_NOT_AVAILABLE,
            ModerationState: ModerationSubmissionState.APPROVED,
            PublicationEligible: true,
            CanUpload: true,
            CanSubmit: false,
            BlockedReasons: []);

        var json = JsonSerializer.Serialize(dto, _webJson);

        json.Should().Contain("\"analysis\":\"ANALYSIS_NOT_AVAILABLE\"");
        json.Should().Contain("\"moderationState\":\"APPROVED\"");

        SellerVersionReviewDto? parsed = JsonSerializer.Deserialize<SellerVersionReviewDto>(json, _webJson);
        parsed.Should().NotBeNull();
        parsed.ModerationState.Should().Be(ModerationSubmissionState.APPROVED);
        parsed.Analysis.Should().Be(SellerAnalysisAvailability.ANALYSIS_NOT_AVAILABLE);
    }

    [Fact]
    public void Deserialize_WhenDeclarationSnapshotCarriesDeclaration_ShouldRoundtrip()
    {
        var dto = new SellerDeclarationSnapshotDto(
            _assetId, _versionId, _workspaceId, 4, 2, _thirdPartyDeclaration, DeclarationComplete: true);

        var json = JsonSerializer.Serialize(dto, _webJson);
        SellerDeclarationSnapshotDto? parsed = JsonSerializer.Deserialize<SellerDeclarationSnapshotDto>(json, _webJson);

        parsed.Should().NotBeNull();
        parsed.AssetVersionId.Should().Be(_versionId);
        parsed.WorkspaceRevision.Should().Be(4);
        parsed.Declaration.Should().NotBeNull();
        parsed.Declaration!.Components.Should().ContainSingle(c => c.Origin == SourceComponentOrigin.THIRD_PARTY);
        parsed.DeclarationComplete.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_WhenDeclarationSnapshotHasNoDeclaration_ShouldKeepNull()
    {
        var dto = new SellerDeclarationSnapshotDto(
            _assetId, null, _workspaceId, 1, 0, Declaration: null, DeclarationComplete: false);

        var json = JsonSerializer.Serialize(dto, _webJson);
        SellerDeclarationSnapshotDto? parsed = JsonSerializer.Deserialize<SellerDeclarationSnapshotDto>(json, _webJson);

        parsed.Should().NotBeNull();
        parsed.AssetVersionId.Should().BeNull();
        parsed.Declaration.Should().BeNull();
        parsed.DeclarationComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData("NOT_AN_ORIGIN")]
    [InlineData("UNKNOWN")]
    public void Deserialize_WhenOriginIsNotAMemberOfSourceComponentOrigin_ShouldFailBinding(string origin)
    {
        // Unknown origin values must fail binding, not coerce to a default member.
        var componentJson = $$"""
            {
              "ownContributionSummary": "s",
              "ownChanges": null,
              "earlierWork": null,
              "redistributionAcknowledged": false,
              "disclosurePolicyVersion": "1",
              "components": [
                {
                  "componentId": "c1",
                  "name": "left-pad",
                  "packagePathOrRange": null,
                  "sourceUrl": "https://example.com/x",
                  "knownVersion": null,
                  "license": "MIT",
                  "noticeLocations": [],
                  "modifications": null,
                  "permissionEvidenceReferences": [],
                  "origin": "{{origin}}"
                }
              ]
            }
            """;

        Action act = () => JsonSerializer.Deserialize<SourceDeclarationPayload>(componentJson, _webJson);
        act.Should().Throw<JsonException>();
    }
}
