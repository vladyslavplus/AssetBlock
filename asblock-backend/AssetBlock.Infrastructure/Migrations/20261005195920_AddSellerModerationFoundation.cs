using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSellerModerationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RoleRevision",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicationSnapshotId",
                table: "purchases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicationSnapshotId",
                table: "order_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicationSnapshotId",
                table: "checkout_intent_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentPublicationSnapshotId",
                table: "assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "asset_draft_workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceRevision = table.Column<long>(type: "bigint", nullable: false),
                    MaterialMetadataHeadRevision = table.Column<int>(type: "integer", nullable: false),
                    SourceDeclarationHeadRevision = table.Column<int>(type: "integer", nullable: false),
                    SellerEvidenceHeadRevision = table.Column<int>(type: "integer", nullable: false),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_draft_workspaces", x => x.Id);
                    table.UniqueConstraint("AK_asset_draft_workspaces_AssetId_Id_WorkspaceVersionScopeKey", x => new { x.AssetId, x.Id, x.WorkspaceVersionScopeKey });
                    table.CheckConstraint("CK_asset_draft_workspaces_case_revision", "\"CaseRevision\" > 0");
                    table.CheckConstraint("CK_asset_draft_workspaces_revision", "\"WorkspaceRevision\" > 0");
                    table.CheckConstraint("CK_asset_draft_workspaces_version_scope_key", "((\"AssetVersionId\" IS NULL AND \"WorkspaceVersionScopeKey\" = '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"AssetVersionId\" IS NOT NULL AND \"WorkspaceVersionScopeKey\" = \"AssetVersionId\"))");
                    table.ForeignKey(
                        name: "FK_asset_draft_workspaces_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_asset_draft_workspaces_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_report_headers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputRevision = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CanAuthorizePublication = table.Column<bool>(type: "boolean", nullable: false),
                    IsFinalized = table.Column<bool>(type: "boolean", nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_report_headers", x => x.Id);
                    table.UniqueConstraint("AK_code_analysis_report_headers_AssetId_AssetVersionId_Id", x => new { x.AssetId, x.AssetVersionId, x.Id });
                    table.CheckConstraint("CK_code_analysis_report_headers_sha", "length(\"ContentSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_code_analysis_report_headers_asset_versions_AssetId_AssetVe~",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_report_headers_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "json_mutation_idempotency",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestDigest = table.Column<string>(type: "char(64)", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_json_mutation_idempotency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_json_mutation_idempotency_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "paid_checkout_reconciliation_holds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckoutIntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeSessionId = table.Column<string>(type: "text", nullable: true),
                    StripeEventId = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SafePaymentFactsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ItemIdentitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paid_checkout_reconciliation_holds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_paid_checkout_reconciliation_holds_checkout_intents_Checkou~",
                        column: x => x.CheckoutIntentId,
                        principalTable: "checkout_intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_material_metadata_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentDigest = table.Column<string>(type: "char(64)", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSubmitted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_material_metadata_revisions", x => x.Id);
                    table.CheckConstraint("CK_asset_material_metadata_revisions_digest", "length(\"ContentDigest\") = 64");
                    table.CheckConstraint("CK_asset_material_metadata_revisions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_asset_material_metadata_revisions_revision_scope", "((\"AssetVersionId\" IS NULL AND \"WorkspaceVersionScopeKey\" = '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"AssetVersionId\" IS NOT NULL AND \"WorkspaceVersionScopeKey\" = \"AssetVersionId\"))");
                    table.ForeignKey(
                        name: "FK_asset_material_metadata_revisions_asset_draft_workspaces_As~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_asset_material_metadata_revisions_asset_versions_AssetId_As~",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_asset_material_metadata_revisions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_seller_evidence_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentDigest = table.Column<string>(type: "char(64)", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSubmitted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_seller_evidence_revisions", x => x.Id);
                    table.CheckConstraint("CK_asset_seller_evidence_revisions_digest", "length(\"ContentDigest\") = 64");
                    table.CheckConstraint("CK_asset_seller_evidence_revisions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_asset_seller_evidence_revisions_revision_scope", "((\"AssetVersionId\" IS NULL AND \"WorkspaceVersionScopeKey\" = '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"AssetVersionId\" IS NOT NULL AND \"WorkspaceVersionScopeKey\" = \"AssetVersionId\"))");
                    table.ForeignKey(
                        name: "FK_asset_seller_evidence_revisions_asset_draft_workspaces_Asse~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_asset_seller_evidence_revisions_asset_versions_AssetId_Asse~",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_asset_seller_evidence_revisions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_source_declaration_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentDigest = table.Column<string>(type: "char(64)", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSubmitted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_source_declaration_revisions", x => x.Id);
                    table.CheckConstraint("CK_asset_source_declaration_revisions_digest", "length(\"ContentDigest\") = 64");
                    table.CheckConstraint("CK_asset_source_declaration_revisions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_asset_source_declaration_revisions_revision_scope", "((\"AssetVersionId\" IS NULL AND \"WorkspaceVersionScopeKey\" = '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"AssetVersionId\" IS NOT NULL AND \"WorkspaceVersionScopeKey\" = \"AssetVersionId\"))");
                    table.ForeignKey(
                        name: "FK_asset_source_declaration_revisions_asset_draft_workspaces_A~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_asset_source_declaration_revisions_asset_versions_AssetId_A~",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_asset_source_declaration_revisions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moderation_submissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    CodeAnalysisReportHeaderId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeclarationRevision = table.Column<int>(type: "integer", nullable: false),
                    MaterialMetadataRevision = table.Column<int>(type: "integer", nullable: false),
                    SellerEvidenceRevision = table.Column<int>(type: "integer", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SellerEvidenceDigest = table.Column<string>(type: "char(64)", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WithdrawalReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    PreviousSubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_submissions", x => x.Id);
                    table.UniqueConstraint("AK_moderation_submissions_AssetId_AssetVersionId_Id", x => new { x.AssetId, x.AssetVersionId, x.Id });
                    table.CheckConstraint("CK_moderation_submissions_case_revision", "\"CaseRevision\" > 0");
                    table.CheckConstraint("CK_moderation_submissions_content_sha", "length(\"ContentSha256\") = 64");
                    table.CheckConstraint("CK_moderation_submissions_evidence_digest", "length(\"SellerEvidenceDigest\") = 64");
                    table.CheckConstraint("CK_moderation_submissions_workspace_scope", "\"AssetVersionId\" = \"WorkspaceVersionScopeKey\"");
                    table.ForeignKey(
                        name: "FK_moderation_submissions_asset_draft_workspaces_AssetId_Works~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_code_analysis_report_headers_AssetId~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.CodeAnalysisReportHeaderId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_moderation_submissions_AssetId_Asset~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.PreviousSubmissionId },
                        principalTable: "moderation_submissions",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moderation_submission_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WithdrawalReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_submission_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_moderation_submission_history_moderation_submissions_Submis~",
                        column: x => x.SubmissionId,
                        principalTable: "moderation_submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "publication_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    CodeAnalysisReportHeaderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModerationSubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovedMetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    RightsReferenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publication_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_publication_snapshots_AssetId_Id", x => new { x.AssetId, x.Id });
                    table.CheckConstraint("CK_publication_snapshots_sha", "length(\"ContentSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_publication_snapshots_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_publication_snapshots_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_publication_snapshots_code_analysis_report_headers_AssetId_~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.CodeAnalysisReportHeaderId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_publication_snapshots_moderation_submissions_AssetId_AssetV~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.ModerationSubmissionId },
                        principalTable: "moderation_submissions",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moderation_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicationSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_moderation_decisions_moderation_submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "moderation_submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_decisions_publication_snapshots_PublicationSnaps~",
                        column: x => x.PublicationSnapshotId,
                        principalTable: "publication_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_purchases_PublicationSnapshotId",
                table: "purchases",
                column: "PublicationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_PublicationSnapshotId",
                table: "order_lines",
                column: "PublicationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_PublicationSnapshotId",
                table: "checkout_intent_items",
                column: "PublicationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_assets_Id_CurrentPublicationSnapshotId",
                table: "assets",
                columns: new[] { "Id", "CurrentPublicationSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "UIX_asset_draft_workspaces_asset_pre_upload",
                table: "asset_draft_workspaces",
                column: "AssetId",
                unique: true,
                filter: "\"AssetVersionId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_draft_workspaces_asset_version",
                table: "asset_draft_workspaces",
                columns: new[] { "AssetId", "AssetVersionId" },
                unique: true,
                filter: "\"AssetVersionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_asset_material_metadata_revisions_AssetId_AssetVersionId",
                table: "asset_material_metadata_revisions",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_material_metadata_revisions_AssetId_WorkspaceId_Revis~",
                table: "asset_material_metadata_revisions",
                columns: new[] { "AssetId", "WorkspaceId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_material_metadata_revisions_AssetId_WorkspaceId_Works~",
                table: "asset_material_metadata_revisions",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_material_metadata_revisions_WorkspaceId_Revision",
                table: "asset_material_metadata_revisions",
                columns: new[] { "WorkspaceId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_seller_evidence_revisions_AssetId_AssetVersionId",
                table: "asset_seller_evidence_revisions",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_seller_evidence_revisions_AssetId_WorkspaceId_Workspa~",
                table: "asset_seller_evidence_revisions",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_seller_evidence_revisions_WorkspaceId_Revision",
                table: "asset_seller_evidence_revisions",
                columns: new[] { "WorkspaceId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_source_declaration_revisions_AssetId_AssetVersionId",
                table: "asset_source_declaration_revisions",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_source_declaration_revisions_AssetId_WorkspaceId_Work~",
                table: "asset_source_declaration_revisions",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_source_declaration_revisions_WorkspaceId_Revision",
                table: "asset_source_declaration_revisions",
                columns: new[] { "WorkspaceId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_report_headers_AssetId_AssetVersionId_Content~",
                table: "code_analysis_report_headers",
                columns: new[] { "AssetId", "AssetVersionId", "ContentSha256", "PolicyVersion", "InputRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_json_mutation_idempotency_ActorUserId_OperationKind_OperationId",
                table: "json_mutation_idempotency",
                columns: new[] { "ActorUserId", "OperationKind", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_json_mutation_idempotency_ActorUserId_OperationKind_Request~",
                table: "json_mutation_idempotency",
                columns: new[] { "ActorUserId", "OperationKind", "RequestDigest" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_decisions_PublicationSnapshotId",
                table: "moderation_decisions",
                column: "PublicationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_decisions_SubmissionId",
                table: "moderation_decisions",
                column: "SubmissionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submission_history_SubmissionId_CreatedAt_Id",
                table: "moderation_submission_history",
                columns: new[] { "SubmissionId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_AssetId_AssetVersionId_CodeAnalysisR~",
                table: "moderation_submissions",
                columns: new[] { "AssetId", "AssetVersionId", "CodeAnalysisReportHeaderId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_AssetId_AssetVersionId_PreviousSubmi~",
                table: "moderation_submissions",
                columns: new[] { "AssetId", "AssetVersionId", "PreviousSubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_AssetId_WorkspaceId_WorkspaceVersion~",
                table: "moderation_submissions",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "UIX_moderation_submissions_active_case",
                table: "moderation_submissions",
                column: "AssetVersionId",
                unique: true,
                filter: "\"State\" IN ('SUBMITTED', 'IN_REVIEW')");

            migrationBuilder.CreateIndex(
                name: "IX_paid_checkout_reconciliation_holds_CheckoutIntentId",
                table: "paid_checkout_reconciliation_holds",
                column: "CheckoutIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_paid_checkout_reconciliation_holds_StripeEventId",
                table: "paid_checkout_reconciliation_holds",
                column: "StripeEventId",
                unique: true,
                filter: "\"StripeEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_publication_snapshots_AssetId_AssetVersionId_CodeAnalysisRe~",
                table: "publication_snapshots",
                columns: new[] { "AssetId", "AssetVersionId", "CodeAnalysisReportHeaderId" });

            migrationBuilder.CreateIndex(
                name: "IX_publication_snapshots_AssetId_AssetVersionId_ModerationSubm~",
                table: "publication_snapshots",
                columns: new[] { "AssetId", "AssetVersionId", "ModerationSubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_publication_snapshots_ModerationSubmissionId",
                table: "publication_snapshots",
                column: "ModerationSubmissionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_assets_publication_snapshots_Id_CurrentPublicationSnapshotId",
                table: "assets",
                columns: new[] { "Id", "CurrentPublicationSnapshotId" },
                principalTable: "publication_snapshots",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_checkout_intent_items_publication_snapshots_PublicationSnap~",
                table: "checkout_intent_items",
                column: "PublicationSnapshotId",
                principalTable: "publication_snapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_order_lines_publication_snapshots_PublicationSnapshotId",
                table: "order_lines",
                column: "PublicationSnapshotId",
                principalTable: "publication_snapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_purchases_publication_snapshots_PublicationSnapshotId",
                table: "purchases",
                column: "PublicationSnapshotId",
                principalTable: "publication_snapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_assets_publication_snapshots_Id_CurrentPublicationSnapshotId",
                table: "assets");

            migrationBuilder.DropForeignKey(
                name: "FK_checkout_intent_items_publication_snapshots_PublicationSnap~",
                table: "checkout_intent_items");

            migrationBuilder.DropForeignKey(
                name: "FK_order_lines_publication_snapshots_PublicationSnapshotId",
                table: "order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_purchases_publication_snapshots_PublicationSnapshotId",
                table: "purchases");

            migrationBuilder.DropTable(
                name: "asset_material_metadata_revisions");

            migrationBuilder.DropTable(
                name: "asset_seller_evidence_revisions");

            migrationBuilder.DropTable(
                name: "asset_source_declaration_revisions");

            migrationBuilder.DropTable(
                name: "json_mutation_idempotency");

            migrationBuilder.DropTable(
                name: "moderation_decisions");

            migrationBuilder.DropTable(
                name: "moderation_submission_history");

            migrationBuilder.DropTable(
                name: "paid_checkout_reconciliation_holds");

            migrationBuilder.DropTable(
                name: "publication_snapshots");

            migrationBuilder.DropTable(
                name: "moderation_submissions");

            migrationBuilder.DropTable(
                name: "asset_draft_workspaces");

            migrationBuilder.DropTable(
                name: "code_analysis_report_headers");

            migrationBuilder.DropIndex(
                name: "IX_purchases_PublicationSnapshotId",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_order_lines_PublicationSnapshotId",
                table: "order_lines");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intent_items_PublicationSnapshotId",
                table: "checkout_intent_items");

            migrationBuilder.DropIndex(
                name: "IX_assets_Id_CurrentPublicationSnapshotId",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "RoleRevision",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PublicationSnapshotId",
                table: "purchases");

            migrationBuilder.DropColumn(
                name: "PublicationSnapshotId",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "PublicationSnapshotId",
                table: "checkout_intent_items");

            migrationBuilder.DropColumn(
                name: "CurrentPublicationSnapshotId",
                table: "assets");
        }
    }
}
