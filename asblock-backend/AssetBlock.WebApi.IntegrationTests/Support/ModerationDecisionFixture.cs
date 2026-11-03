using System.Net.Http.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Support;

/// <summary>Seeds a submission to the state where a Moderator can claim and decide it through HTTP.</summary>
internal static class ModerationDecisionFixture
{
    /// <summary>Writes the draft material revision the decision reads (title and description default to the historical fixture text).</summary>
    public static async Task<Guid> PrepareDecisionReadySubmission(
        ApplicationDbContext db,
        ModerationSubmission submission,
        string draftTitle = "Pending public title",
        string? draftDescription = null,
        CodeAnalysisCoverageLevel coverageLevel = CodeAnalysisCoverageLevel.FULL,
        int omissionCount = 0)
    {
        AssetVersion version = await db.AssetVersions.SingleAsync(v => v.Id == submission.AssetVersionId);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.SingleAsync(w => w.Id == submission.WorkspaceId);
        Asset asset = await db.Assets.SingleAsync(a => a.Id == submission.AssetId);
        Category category = await db.Categories.SingleAsync(c => c.Id == asset.CategoryId);
        workspace.SourceDeclarationHeadRevision = 1;
        workspace.MaterialMetadataHeadRevision = 1;
        var declarationId = Guid.NewGuid();
        var materialId = Guid.NewGuid();
        const string declaration = """{"redistributionAcknowledged":true,"ownContributionSummary":"Original implementation.","components":[]}""";
        var material = System.Text.Json.JsonSerializer.Serialize(new { title = draftTitle, description = draftDescription, categoryId = category.Id, tags = Array.Empty<string>() });
        var declarationDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(declaration)));
        var materialDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        db.AssetSourceDeclarationRevisions.Add(new AssetSourceDeclarationRevision
        {
            Id = declarationId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            Revision = 1,
            AuthorUserId = submission.OwnerUserId,
            ContentDigest = declarationDigest,
            SchemaVersion = 1,
            PayloadJson = declaration,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
        {
            Id = materialId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            Revision = 1,
            AuthorUserId = submission.OwnerUserId,
            ContentDigest = materialDigest,
            SchemaVersion = 1,
            PayloadJson = material,
            CreatedAt = DateTimeOffset.UtcNow
        });
        AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Chain chain =
            AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.BuildChain() with { PolicyDigest = ReviewAnalysisPolicy.DIGEST };
        CodeAnalysisPolicyVersion? policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleOrDefaultAsync(p => p.PolicyKey == ReviewAnalysisPolicy.KEY && p.PolicyDigest == ReviewAnalysisPolicy.DIGEST);
        if (policy is null)
        { db.CodeAnalysisPolicyVersions.Add(AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Policy(chain)); }
        else
        { chain = chain with { PolicyVersionId = policy.Id }; }
        AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.SeedObservedProof(db, version, chain);
        await db.SaveChangesAsync();
        CodeAnalysisRun run = AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.CompletedRun(version, chain);
        run.AssetDraftWorkspaceId = workspace.Id;
        run.DeclarationRevisionId = declarationId;
        run.MaterialRevisionId = materialId;
        run.DeclarationDigest = declarationDigest;
        run.MaterialDigest = materialDigest;
        db.CodeAnalysisRuns.Add(run);
        await db.SaveChangesAsync();
        CodeAnalysisReportHeader report = AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Header(version, chain);
        report.Disposition = CodeAnalysisReportDisposition.SELECTED;
        report.AnalyzedFileCount = 1;
        report.CoverageLevel = coverageLevel;
        report.OmissionCount = omissionCount;
        report.FindingCount = 0;
        db.CodeAnalysisReportHeaders.Add(report);
        await db.SaveChangesAsync();
        CodeAnalysisReportAdmission admission = AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Admission(report, chain, true);
        admission.AdmittedBy = "assetblock-dotnet-finalizer";
        db.CodeAnalysisReportAdmissions.Add(admission);
        foreach (var stage in AssetBlock.Infrastructure.Persistence.Stores.CodeAnalysisReportFinalizer.StageNames)
        {
            db.CodeAnalysisReportStages.Add(new CodeAnalysisReportStage
            {
                Id = Guid.NewGuid(),
                ReportId = report.Id,
                Stage = stage,
                Applicability = CodeAnalysisStageApplicability.MANDATORY,
                Outcome = stage == "VERIFIER" ? CodeAnalysisStageOutcome.NOT_APPLICABLE : CodeAnalysisStageOutcome.SUCCEEDED,
                ErrorCode = stage == "VERIFIER" ? "POLICY_DISABLED" : null,
                Retryable = false,
                DurationMilliseconds = 0,
                ToolIdentityJson = "{}",
                CountsJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        db.Entry(submission).Property(s => s.CodeAnalysisReportHeaderId).CurrentValue = report.Id;
        db.Entry(submission).Property(s => s.SellerEvidenceDigest).CurrentValue = AssetBlock.Infrastructure.Persistence.ProductDigests.EmptyProjectionDigest(nameof(AssetSellerEvidenceRevision));
        await db.SaveChangesAsync();
        await db.AssetVersions.Where(candidate => candidate.Id == version.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.SelectedCodeAnalysisReportId, report.Id));
        return report.Id;
    }

    public static async Task<object> ReadDecisionBasis(IServiceScopeFactory scopeFactory, Guid submissionId, Guid reportId)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var basis = await db.ModerationSubmissions.AsNoTracking().Where(s => s.Id == submissionId)
            .Select(s => new { s.AssetVersionId, s.ContentSha256, s.DeclarationRevision, s.MaterialMetadataRevision, s.PolicyVersion, s.SellerEvidenceRevision, s.CaseRevision })
            .SingleAsync();
        return new
        {
            versionId = basis.AssetVersionId,
            contentSha256 = basis.ContentSha256,
            analysisReportId = reportId,
            declarationRevision = basis.DeclarationRevision,
            materialMetadataRevision = basis.MaterialMetadataRevision,
            policyVersion = basis.PolicyVersion,
            sellerEvidenceRevision = basis.SellerEvidenceRevision,
            caseRevision = basis.CaseRevision
        };
    }

    /// <summary>Adds a SUBMITTED case with its own draft workspace for one version.</summary>
    public static ModerationSubmission AddReviewableSubmission(ApplicationDbContext db, Asset asset, AssetVersion version, User owner)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var workspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            ScopeId = Guid.NewGuid(),
            CreatedAt = now
        };
        var submission = new ModerationSubmission
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            OwnerUserId = owner.Id,
            ContentSha256 = version.ContentSha256,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 0,
            SellerEvidenceDigest = new string('e', 64),
            PolicyVersion = ReviewAnalysisPolicy.KEY,
            State = ModerationSubmissionState.SUBMITTED,
            CaseRevision = 1,
            CreatedAt = now
        };
        db.AssetDraftWorkspaces.Add(workspace);
        db.ModerationSubmissions.Add(submission);
        return submission;
    }

    public static Task<HttpResponseMessage> PostClaimAsync(HttpClient reviewer, Guid submissionId, Guid operationId, long expectedCaseRevision)
    {
        var path = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
        return reviewer.PostAsJsonAsync(path, new { operationId, expectedCaseRevision });
    }

    /// <summary>Decision body. Replays reuse the exact object so the request digest matches.</summary>
    public static object DecisionBody(Guid operationId, object basis, string outcome) => new
    {
        operationId,
        basis,
        outcome,
        rationale = "Reviewed for the requested outcome.",
        dispositions = Array.Empty<object>(),
        manualPlanCompletionReference = (string?)null
    };

    public static Task<HttpResponseMessage> PostDecisionAsync(HttpClient reviewer, Guid submissionId, object body)
    {
        var path = "/" + ApiRoutes.Moderation.DECISION.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
        return reviewer.PostAsJsonAsync(path, body);
    }
}
