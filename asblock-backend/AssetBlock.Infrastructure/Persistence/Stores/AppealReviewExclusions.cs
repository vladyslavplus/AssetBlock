using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class AppealReviewExclusion
{
    public Guid SubmissionId { get; init; }
    public Guid UserId { get; init; }
}

/// <summary>SQL-composable appeal reviewer exclusions. Invalid appeal links fail closed.</summary>
internal static class AppealReviewExclusions
{
    public static IQueryable<Guid> ForSubmission(ApplicationDbContext db, Guid submissionId) =>
        All(db).Where(x => x.SubmissionId == submissionId).Select(x => x.UserId);

    public static IQueryable<AppealReviewExclusion> All(ApplicationDbContext db) => db.Database.SqlQueryRaw<AppealReviewExclusion>("""
        WITH invalid AS (
            SELECT s."Id" AS appeal_id
            FROM moderation_submissions s
            LEFT JOIN moderation_decisions d ON d."Id" = s."AppealOfDecisionId"
            WHERE s."Kind" = 'APPEAL' AND (d."Id" IS NULL OR d."Outcome" <> 'REJECTED'
                OR d."SubmissionId" IS DISTINCT FROM s."PreviousSubmissionId"
                OR d."AssetId" <> s."AssetId" OR d."AssetVersionId" <> s."AssetVersionId")
        )
        SELECT s."Id" AS "SubmissionId", s."OwnerUserId" AS "UserId"
        FROM moderation_submissions s WHERE s."Kind" = 'APPEAL'
        UNION
        SELECT s."Id", a."AuthorId" FROM moderation_submissions s JOIN assets a ON a."Id" = s."AssetId" WHERE s."Kind" = 'APPEAL'
        UNION
        SELECT s."Id", d."ModeratorUserId" FROM moderation_submissions s JOIN moderation_decisions d ON d."Id" = s."AppealOfDecisionId" WHERE s."Kind" = 'APPEAL'
        UNION
        SELECT s."Id", d."ModeratorUserId"
        FROM moderation_submissions s
        JOIN moderation_decisions appealed ON appealed."Id" = s."AppealOfDecisionId"
        JOIN moderation_submissions prior ON prior."AssetVersionId" = s."AssetVersionId"
        JOIN moderation_decisions d ON d."SubmissionId" = prior."Id" AND d."CreatedAt" <= appealed."CreatedAt"
        WHERE s."Kind" = 'APPEAL'
        UNION
        SELECT i.appeal_id, u."Id" FROM invalid i CROSS JOIN users u
        """);
}
