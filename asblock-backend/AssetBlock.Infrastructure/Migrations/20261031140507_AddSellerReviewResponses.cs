using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSellerReviewResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_code_analysis_report_findings_ReportId_Id",
                table: "code_analysis_report_findings",
                columns: new[] { "ReportId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_asset_seller_evidence_revisions_WorkspaceId_Revision",
                table: "asset_seller_evidence_revisions",
                columns: new[] { "WorkspaceId", "Revision" });

            migrationBuilder.CreateTable(
                name: "moderation_finding_dispositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_finding_dispositions", x => x.Id);
                    table.CheckConstraint("CK_finding_dispositions_rationale", "char_length(\"Rationale\") BETWEEN 1 AND 2000 AND btrim(\"Rationale\") = \"Rationale\"");
                    table.CheckConstraint("CK_finding_dispositions_revision", "\"CaseRevision\" > 0");
                    table.CheckConstraint("CK_finding_dispositions_type", "\"Disposition\" IN ('EXPLAINED','FALSE_MATCH','ACCEPTED_WITH_LIMITS','CHANGES_NEEDED','INSUFFICIENT_EVIDENCE')");
                    table.ForeignKey(
                        name: "FK_moderation_finding_dispositions_code_analysis_report_findin~",
                        columns: x => new { x.ReportId, x.FindingId },
                        principalTable: "code_analysis_report_findings",
                        principalColumns: new[] { "ReportId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_finding_dispositions_code_analysis_report_header~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.ReportId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_finding_dispositions_moderation_submissions_Asse~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.SubmissionId },
                        principalTable: "moderation_submissions",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_finding_dispositions_users_ModeratorUserId",
                        column: x => x.ModeratorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "seller_responses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerEvidenceRevision = table.Column<int>(type: "integer", nullable: false),
                    ResponseType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_responses", x => x.Id);
                    table.CheckConstraint("CK_seller_responses_revision", "\"SellerEvidenceRevision\" > 0");
                    table.CheckConstraint("CK_seller_responses_scope", "\"AssetVersionId\" = \"WorkspaceVersionScopeKey\"");
                    table.CheckConstraint("CK_seller_responses_text", "char_length(\"Text\") <= 2000 AND btrim(\"Text\") = \"Text\" AND (\"ResponseType\" = 'NO_EVIDENCE' OR char_length(\"Text\") > 0)");
                    table.CheckConstraint("CK_seller_responses_type", "\"ResponseType\" IN ('PRIOR_WORK','PERMITTED_COMPONENT','INDEPENDENT_IMPLEMENTATION','WRONG_SOURCE','CORRECTED_FILES','NO_EVIDENCE')");
                    table.ForeignKey(
                        name: "FK_seller_responses_asset_draft_workspaces_AssetId_WorkspaceId~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_asset_seller_evidence_revisions_WorkspaceI~",
                        columns: x => new { x.WorkspaceId, x.SellerEvidenceRevision },
                        principalTable: "asset_seller_evidence_revisions",
                        principalColumns: new[] { "WorkspaceId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_code_analysis_report_findings_ReportId_Fin~",
                        columns: x => new { x.ReportId, x.FindingId },
                        principalTable: "code_analysis_report_findings",
                        principalColumns: new[] { "ReportId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_code_analysis_report_headers_AssetId_Asset~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.ReportId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_AssetId_AssetVersionId_Repo~",
                table: "moderation_finding_dispositions",
                columns: new[] { "AssetId", "AssetVersionId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_AssetId_AssetVersionId_Subm~",
                table: "moderation_finding_dispositions",
                columns: new[] { "AssetId", "AssetVersionId", "SubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_ModeratorUserId",
                table: "moderation_finding_dispositions",
                column: "ModeratorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_ReportId_FindingId",
                table: "moderation_finding_dispositions",
                columns: new[] { "ReportId", "FindingId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_SubmissionId_FindingId_Case~",
                table: "moderation_finding_dispositions",
                columns: new[] { "SubmissionId", "FindingId", "CaseRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_ActorUserId",
                table: "seller_responses",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_AssetId_AssetVersionId_ReportId",
                table: "seller_responses",
                columns: new[] { "AssetId", "AssetVersionId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_AssetId_WorkspaceId_WorkspaceVersionScopeK~",
                table: "seller_responses",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_AssetVersionId_SellerEvidenceRevision_Id",
                table: "seller_responses",
                columns: new[] { "AssetVersionId", "SellerEvidenceRevision", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_ReportId_FindingId",
                table: "seller_responses",
                columns: new[] { "ReportId", "FindingId" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_WorkspaceId_SellerEvidenceRevision_Finding~",
                table: "seller_responses",
                columns: new[] { "WorkspaceId", "SellerEvidenceRevision", "FindingId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "moderation_finding_dispositions");

            migrationBuilder.DropTable(
                name: "seller_responses");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_code_analysis_report_findings_ReportId_Id",
                table: "code_analysis_report_findings");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_asset_seller_evidence_revisions_WorkspaceId_Revision",
                table: "asset_seller_evidence_revisions");
        }
    }
}
