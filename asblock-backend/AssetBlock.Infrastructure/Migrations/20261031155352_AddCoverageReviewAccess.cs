using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoverageReviewAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CoverageAssessmentId",
                table: "moderation_submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "coverage_review_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    DeclarationRevision = table.Column<int>(type: "integer", nullable: false),
                    MaterialRevision = table.Column<int>(type: "integer", nullable: false),
                    SellerEvidenceRevision = table.Column<int>(type: "integer", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PolicyDigest = table.Column<string>(type: "char(64)", nullable: false),
                    TupleDigest = table.Column<string>(type: "char(64)", nullable: false),
                    RequestedWorkspaceRevision = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coverage_review_requests", x => x.Id);
                    table.CheckConstraint("CK_coverage_requests_digests", "length(\"TupleDigest\") = 64 AND length(\"ContentSha256\") = 64 AND length(\"PolicyDigest\") = 64");
                    table.CheckConstraint("CK_coverage_requests_revision", "\"RequestedWorkspaceRevision\" > 0 AND \"Revision\" > 0 AND \"DeclarationRevision\" > 0 AND \"MaterialRevision\" > 0 AND \"SellerEvidenceRevision\" >= 0");
                    table.ForeignKey(
                        name: "FK_coverage_review_requests_asset_draft_workspaces_AssetId_Wor~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.AssetVersionId },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coverage_review_requests_asset_versions_AssetId_AssetVersio~",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coverage_review_requests_code_analysis_report_headers_Asset~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.ReportId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coverage_review_requests_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "coverage_assessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompetenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    ScopeJson = table.Column<string>(type: "jsonb", nullable: false),
                    ManageableScope = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewPlan = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CompletedPlanReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coverage_assessments", x => x.Id);
                    table.CheckConstraint("CK_coverage_assessments_outcome", "\"Outcome\" IN ('ACCEPTED','BLOCKED')");
                    table.CheckConstraint("CK_coverage_assessments_plan", "\"Outcome\" <> 'ACCEPTED' OR (\"ManageableScope\" = TRUE AND length(\"CompletedPlanReference\") > 0)");
                    table.CheckConstraint("CK_coverage_assessments_text", "length(\"Rationale\") BETWEEN 1 AND 2000 AND length(\"ReviewPlan\") BETWEEN 1 AND 2000 AND length(\"CompletedPlanReference\") <= 200");
                    table.ForeignKey(
                        name: "FK_coverage_assessments_coverage_review_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "coverage_review_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coverage_assessments_users_ModeratorUserId",
                        column: x => x.ModeratorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_CoverageAssessmentId",
                table: "moderation_submissions",
                column: "CoverageAssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_coverage_assessments_ModeratorUserId",
                table: "coverage_assessments",
                column: "ModeratorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_coverage_assessments_RequestId",
                table: "coverage_assessments",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_AssetId_AssetVersionId_ReportId",
                table: "coverage_review_requests",
                columns: new[] { "AssetId", "AssetVersionId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_AssetId_WorkspaceId_AssetVersionId",
                table: "coverage_review_requests",
                columns: new[] { "AssetId", "WorkspaceId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_AssetVersionId_RequestedWorkspaceR~",
                table: "coverage_review_requests",
                columns: new[] { "AssetVersionId", "RequestedWorkspaceRevision", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_OwnerUserId",
                table: "coverage_review_requests",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_TupleDigest",
                table: "coverage_review_requests",
                column: "TupleDigest",
                unique: true,
                filter: "\"IsOpen\" = TRUE");

            migrationBuilder.AddForeignKey(
                name: "FK_moderation_submissions_coverage_assessments_CoverageAssessm~",
                table: "moderation_submissions",
                column: "CoverageAssessmentId",
                principalTable: "coverage_assessments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_moderation_submissions_coverage_assessments_CoverageAssessm~",
                table: "moderation_submissions");

            migrationBuilder.DropTable(
                name: "coverage_assessments");

            migrationBuilder.DropTable(
                name: "coverage_review_requests");

            migrationBuilder.DropIndex(
                name: "IX_moderation_submissions_CoverageAssessmentId",
                table: "moderation_submissions");

            migrationBuilder.DropColumn(
                name: "CoverageAssessmentId",
                table: "moderation_submissions");
        }
    }
}
