namespace AssetBlock.Domain.Core.Constants;

public static class ModerationOperationKinds
{
    public const string APPEAL_CREATE = "Moderation.AppealCreate";
    public const string CASE_DECIDE = "Moderation.CaseDecide";
    public const string CASE_CLAIM = "Moderation.CaseClaim";
    public const string CASE_RELEASE = "Moderation.CaseRelease";
    public const string COVERAGE_REQUEST = "coverage-request";
    public const string COVERAGE_ASSESS = "coverage-assess";

    public const string RESPONSE_SAVE = "Moderation.ResponseSave";
    public const string EVIDENCE_SAVE = "Moderation.EvidenceSave";
    public const string DRAFT_CREATE = "Moderation.DraftCreate";
    public const string DRAFT_SAVE = "Moderation.DraftSave";
    public const string DECLARATION_SAVE = "Moderation.DeclarationSave";
    public const string SUBMISSION_CREATE = "Moderation.SubmissionCreate";
    public const string SUBMISSION_WITHDRAW = "Moderation.SubmissionWithdraw";
}
