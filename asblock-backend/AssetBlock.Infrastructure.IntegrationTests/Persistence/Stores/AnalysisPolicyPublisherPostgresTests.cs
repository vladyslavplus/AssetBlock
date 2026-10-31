using System.Text.Json.Nodes;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Proves the approved analysis policy and runtime manifest is published as one immutable,
/// content-addressed row: an identical manifest republishes as a no-op, two publishers racing on the
/// same key can never leave two approved identities behind, changed content is never edited in
/// place, and a manifest that only fills the columns with empty objects or omits the pinned
/// encoder, reference or stage identity is refused without writing.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalysisPolicyPublisherPostgresTests(PostgresFixture fixture)
{
    private static AnalysisPolicyPublisher CreatePublisher(ApplicationDbContext db) =>
        new(db, NullLogger<AnalysisPolicyPublisher>.Instance);

    private static AnalysisPolicyManifest Manifest(
        string policyKey = "code-analysis-policy-v1",
        string? policyDigest = null,
        string schemaName = "code-analysis-v1",
        string stageMatrixJson = "",
        string toolIdentitiesJson = "",
        string limitsJson = "",
        string securityValidityJson = "",
        string verifierReason = "Retrieval quality research evaluation has not been reviewed.",
        bool omitModelKey = false,
        bool omitIndexKey = false,
        bool omitReferenceSnapshot = false) =>
        new(policyKey,
            policyDigest ?? AdmittedReportTestEvidence.NewHex64(),
            schemaName,
            stageMatrixJson.Length == 0 ? AdmittedReportTestEvidence.StageMatrixJson() : stageMatrixJson,
            "DISABLED_BY_EVALUATION",
            verifierReason,
            toolIdentitiesJson.Length == 0 ? AdmittedReportTestEvidence.ToolIdentitiesJson() : toolIdentitiesJson,
            limitsJson.Length == 0 ? AdmittedReportTestEvidence.LimitsJson() : limitsJson,
            securityValidityJson.Length == 0 ? AdmittedReportTestEvidence.SecurityValidityJson() : securityValidityJson,
            omitModelKey ? null : AdmittedReportTestEvidence.NewHex64(),
            omitIndexKey ? null : AdmittedReportTestEvidence.NewHex64(),
            omitReferenceSnapshot ? null : "snapshot-2026-10-01");

    [Fact]
    public async Task Publish_WhenManifestIsComplete_ShouldPersistExactlyOneRow()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AnalysisPolicyManifest manifest = Manifest();

        AnalysisPolicyPublishResult published = await CreatePublisher(db).Publish(manifest);

        published.Outcome.Should().Be(AnalysisPolicyPublishOutcome.PUBLISHED);
        published.PolicyVersionId.Should().NotBe(Guid.Empty);

        CodeAnalysisPolicyVersion stored = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        stored.PolicyKey.Should().Be(manifest.PolicyKey);
        stored.PolicyDigest.Should().Be(manifest.PolicyDigest);
        stored.ModelKey.Should().Be(manifest.ModelKey);
        stored.IndexKey.Should().Be(manifest.IndexKey);
        stored.ReferenceSnapshotId.Should().Be(manifest.ReferenceSnapshotId);
    }

    [Fact]
    public async Task Publish_WhenReplayedWithIdenticalManifest_ShouldNotWriteASecondRow()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AnalysisPolicyManifest manifest = Manifest();
        AnalysisPolicyPublisher publisher = CreatePublisher(db);

        AnalysisPolicyPublishResult first = await publisher.Publish(manifest);
        AnalysisPolicyPublishResult replay = await publisher.Publish(manifest);

        replay.Published.Should().BeTrue();
        replay.PolicyVersionId.Should().Be(first.PolicyVersionId);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Publish_WhenSameKeyCarriesAnotherDigest_ShouldRefuseTheSecondIdentity()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AnalysisPolicyPublisher publisher = CreatePublisher(db);

        await publisher.Publish(Manifest());
        AnalysisPolicyPublishResult second = await publisher.Publish(Manifest());

        second.Outcome.Should().Be(AnalysisPolicyPublishOutcome.CONFLICT);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Publish_WhenSameIdentityCarriesChangedContent_ShouldRefuseTheInPlaceEdit()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AnalysisPolicyManifest manifest = Manifest();
        AnalysisPolicyPublisher publisher = CreatePublisher(db);
        await publisher.Publish(manifest);

        AnalysisPolicyPublishResult edited = await publisher.Publish(
            manifest with { ToolIdentitiesJson = AdmittedReportTestEvidence.ToolIdentitiesJson().Replace("encoder-only", "encoder-other") });

        edited.Outcome.Should().Be(AnalysisPolicyPublishOutcome.CONFLICT);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Publish_WhenConcurrentPublishersShareKeyAndDigest_ShouldPersistOneRowAndReplayTheOther()
    {
        await using ApplicationDbContext firstDb = await fixture.CreateCleanDbContext();
        await using ApplicationDbContext secondDb = await fixture.CreateCleanDbContext();
        AnalysisPolicyManifest manifest = Manifest();

        Task<AnalysisPolicyPublishResult> first = CreatePublisher(firstDb).Publish(manifest);
        Task<AnalysisPolicyPublishResult> second = CreatePublisher(secondDb).Publish(manifest);
        await Task.WhenAll(first, second);

        AnalysisPolicyPublishResult firstResult = await first;
        AnalysisPolicyPublishResult secondResult = await second;

        firstResult.Published.Should().BeTrue();
        secondResult.Published.Should().BeTrue();

        // Both publishers converge on the same single approved identity.
        firstResult.PolicyVersionId.Should().Be(secondResult.PolicyVersionId);
        (await firstDb.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Publish_WhenConcurrentPublishersShareKeyWithDifferentDigests_ShouldRefuseTheSecond()
    {
        await using ApplicationDbContext firstDb = await fixture.CreateCleanDbContext();
        await using ApplicationDbContext secondDb = await fixture.CreateCleanDbContext();

        Task<AnalysisPolicyPublishResult> first = CreatePublisher(firstDb).Publish(Manifest());
        Task<AnalysisPolicyPublishResult> second = CreatePublisher(secondDb).Publish(Manifest());
        await Task.WhenAll(first, second);

        AnalysisPolicyPublishResult[] results = [await first, await second];
        results.Count(r => r.Outcome == AnalysisPolicyPublishOutcome.PUBLISHED).Should().Be(1);
        results.Count(r => r.Outcome == AnalysisPolicyPublishOutcome.CONFLICT).Should().Be(1);

        // Serializing on the policy key is what makes one key mean exactly one approved identity.
        (await firstDb.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(1);
        (await firstDb.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync()).PolicyKey
            .Should().Be("code-analysis-policy-v1");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"stages":[]}""")]
    public async Task Publish_WhenStageMatrixIsEmptyOrIncomplete_ShouldBeRejected(string stageMatrixJson)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        AnalysisPolicyPublishResult result = await CreatePublisher(db).Publish(
            Manifest(stageMatrixJson: stageMatrixJson));

        result.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Publish_WhenStageMatrixOmitsAMandatoryStage_ShouldBeRejected()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var sevenStages = RemoveStage(AdmittedReportTestEvidence.StageMatrixJson(), "REPORT");

        AnalysisPolicyPublishResult result = await CreatePublisher(db).Publish(
            Manifest(stageMatrixJson: sevenStages));

        result.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        result.Reason.Should().Contain("REPORT");
    }

    [Theory]
    [InlineData("checkpointSha256")]
    [InlineData("tokenizerHashes")]
    [InlineData("preprocessing")]
    [InlineData("pooling")]
    public async Task Publish_WhenToolIdentityOmitsAFrozenEncoderMember_ShouldBeRejected(string member)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var incomplete = RemoveMember(AdmittedReportTestEvidence.ToolIdentitiesJson(), member);

        AnalysisPolicyPublishResult result = await CreatePublisher(db).Publish(
            Manifest(toolIdentitiesJson: incomplete));

        result.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Publish_WhenToolIdentityIsAnEmptyObject_ShouldBeRejected()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        AnalysisPolicyPublishResult result = await CreatePublisher(db).Publish(
            Manifest(toolIdentitiesJson: "{}"));

        result.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        result.Reason.Should().Contain("checkpointSha256");
    }

    [Theory]
    [InlineData("maxWindows")]
    [InlineData("unionBudget")]
    public async Task Publish_WhenOperationBudgetIsIncomplete_ShouldBeRejected(string member)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        AnalysisPolicyPublishResult result = await CreatePublisher(db).Publish(
            Manifest(limitsJson: RemoveMember(AdmittedReportTestEvidence.LimitsJson(), member)));

        result.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Publish_WhenSecurityValidityIsAnEmptyObject_ShouldBeRejected()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        AnalysisPolicyPublishResult result = await CreatePublisher(db).Publish(
            Manifest(securityValidityJson: "{}"));

        result.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Publish_WhenPinnedReferenceIdentityIsAbsent_ShouldBeRejected()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        AnalysisPolicyPublishResult noModel = await CreatePublisher(db).Publish(
            Manifest(omitModelKey: true));
        AnalysisPolicyPublishResult noIndex = await CreatePublisher(db).Publish(
            Manifest(policyKey: "other-policy", omitIndexKey: true));
        AnalysisPolicyPublishResult noSnapshot = await CreatePublisher(db).Publish(
            Manifest(policyKey: "third-policy", omitReferenceSnapshot: true));

        noModel.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        noIndex.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        noSnapshot.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Publish_WhenPolicyDigestIsNotCanonicalHex_ShouldBeRejected()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        AnalysisPolicyPublishResult result = await CreatePublisher(db).Publish(
            Manifest(policyDigest: new string('A', 64)));

        result.Outcome.Should().Be(AnalysisPolicyPublishOutcome.REJECTED);
        (await db.CodeAnalysisPolicyVersions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    /// <summary>Removes one JSON member so an incomplete manifest can be exercised.</summary>
    private static string RemoveMember(string json, string member)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var remaining = document.RootElement
            .EnumerateObject()
            .Where(p => !string.Equals(p.Name, member, StringComparison.Ordinal))
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        return System.Text.Json.JsonSerializer.Serialize(remaining);
    }

    /// <summary>Drops one declared stage, keeping the remaining stage matrix valid JSON.</summary>
    private static string RemoveStage(string stageMatrixJson, string stage)
    {
        JsonNode matrix = JsonNode.Parse(stageMatrixJson)!;
        var stages = (JsonArray)matrix["stages"]!;
        JsonNode? found = null;
        foreach (JsonNode? entry in stages)
        {
            if (entry!["stage"]!.GetValue<string>() == stage)
            {
                found = entry;
                break;
            }
        }

        stages.Remove(found);
        return matrix.ToJsonString();
    }
}
