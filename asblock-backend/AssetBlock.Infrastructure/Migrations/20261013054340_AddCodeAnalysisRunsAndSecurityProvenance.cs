using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeAnalysisRunsAndSecurityProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_code_analysis_report_headers_AssetId_AssetVersionId_Content~",
                table: "code_analysis_report_headers");

            migrationBuilder.DropIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_processing_jobs_type",
                table: "asset_processing_jobs");

            migrationBuilder.AddColumn<Guid>(
                name: "AnalysisRunId",
                table: "code_analysis_report_headers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnalyzedFileCount",
                table: "code_analysis_report_headers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverageLevel",
                table: "code_analysis_report_headers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Disposition",
                table: "code_analysis_report_headers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FindingCount",
                table: "code_analysis_report_headers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OmissionCount",
                table: "code_analysis_report_headers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PythonReportId",
                table: "code_analysis_report_headers",
                type: "char(64)",
                fixedLength: true,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportDigest",
                table: "code_analysis_report_headers",
                type: "char(64)",
                fixedLength: true,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SelectionGeneration",
                table: "code_analysis_report_headers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnalysisSelectionGeneration",
                table: "asset_versions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SafetyRestrictedAt",
                table: "asset_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SafetyRestriction",
                table: "asset_versions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "NONE");

            migrationBuilder.AddColumn<string>(
                name: "SafetyRestrictionReason",
                table: "asset_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedCodeAnalysisReportId",
                table: "asset_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AnalysisRunId",
                table: "asset_processing_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SecurityRecheckId",
                table: "asset_processing_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransportLeaseToken",
                table: "asset_processing_jobs",
                type: "char(64)",
                fixedLength: true,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "analysis_service_registrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_service_registrations", x => x.Id);
                    table.CheckConstraint("CK_analysis_service_registrations_capacity", "\"Capacity\" > 0");
                    table.CheckConstraint("CK_analysis_service_registrations_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_policy_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    SchemaName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StageMatrixJson = table.Column<string>(type: "jsonb", nullable: false),
                    VerifierMode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    VerifierReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ToolIdentitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    LimitsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SecurityValidityJson = table.Column<string>(type: "jsonb", nullable: false),
                    ModelKey = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    IndexKey = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ReferenceSnapshotId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_policy_versions", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_policy_versions_digest", "\"PolicyDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_policy_versions_limits", "jsonb_typeof(\"LimitsJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_policy_versions_security_validity", "jsonb_typeof(\"SecurityValidityJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_policy_versions_stage_matrix", "jsonb_typeof(\"StageMatrixJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_policy_versions_tool_identities", "jsonb_typeof(\"ToolIdentitiesJson\") = 'object'");
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_report_admissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdmissionKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AdmittedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AdmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    PythonReportId = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    InputDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReservationSlotNumber = table.Column<int>(type: "integer", nullable: false),
                    ReservationGeneration = table.Column<int>(type: "integer", nullable: false),
                    ReservationLeaseTokenDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    CanAuthorizePublication = table.Column<bool>(type: "boolean", nullable: false),
                    PublicationAuthorityReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_report_admissions", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_report_admissions_authority_reason", "\"PublicationAuthorityReason\" IS NOT NULL AND length(\"PublicationAuthorityReason\") > 0");
                    table.CheckConstraint("CK_code_analysis_report_admissions_generation", "\"ReservationGeneration\" > 0");
                    table.CheckConstraint("CK_code_analysis_report_admissions_input_digest", "\"InputDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_kind", "\"AdmissionKind\" = 'SERVER_VALIDATED'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_lease_token_digest", "\"ReservationLeaseTokenDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_python_report_id", "\"PythonReportId\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_report_digest", "\"ReportDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_slot_number", "\"ReservationSlotNumber\" >= 0");
                    table.ForeignKey(
                        name: "FK_code_analysis_report_admissions_code_analysis_report_header~",
                        column: x => x.ReportId,
                        principalTable: "code_analysis_report_headers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_report_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FindingType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    OwnScopePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OwnStartByte = table.Column<int>(type: "integer", nullable: false),
                    OwnEndByte = table.Column<int>(type: "integer", nullable: false),
                    ForeignSourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ForeignSensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PublicAttributionJson = table.Column<string>(type: "jsonb", nullable: true),
                    TokenScore = table.Column<double>(type: "double precision", nullable: true),
                    SemanticScore = table.Column<double>(type: "double precision", nullable: true),
                    TokenRank = table.Column<int>(type: "integer", nullable: true),
                    SemanticRank = table.Column<int>(type: "integer", nullable: true),
                    ReasonText = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    RequestedAction = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_report_findings", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_report_findings_attribution", "\"ForeignSensitivity\" <> 'PRIVATE' OR \"PublicAttributionJson\" IS NULL");
                    table.CheckConstraint("CK_code_analysis_report_findings_attribution_json", "\"PublicAttributionJson\" IS NULL OR jsonb_typeof(\"PublicAttributionJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_report_findings_key", "\"FindingKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                    table.CheckConstraint("CK_code_analysis_report_findings_scores", "\"TokenScore\" IS NULL OR \"TokenScore\" <> 'NaN'::double precision");
                    table.CheckConstraint("CK_code_analysis_report_findings_scores_semantic", "\"SemanticScore\" IS NULL OR \"SemanticScore\" <> 'NaN'::double precision");
                    table.CheckConstraint("CK_code_analysis_report_findings_sensitivity", "\"ForeignSensitivity\" IN ('PUBLIC', 'PRIVATE')");
                    table.CheckConstraint("CK_code_analysis_report_findings_spans", "\"OwnStartByte\" >= 0 AND \"OwnEndByte\" >= \"OwnStartByte\"");
                    table.ForeignKey(
                        name: "FK_code_analysis_report_findings_code_analysis_report_headers_~",
                        column: x => x.ReportId,
                        principalTable: "code_analysis_report_headers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "analysis_capacity_slots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SlotNumber = table.Column<int>(type: "integer", nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReservedRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReservedJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseToken = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ReservationGeneration = table.Column<int>(type: "integer", nullable: true),
                    HardDeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CleanupGraceUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedTerminationAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_capacity_slots", x => x.Id);
                    table.CheckConstraint("CK_analysis_capacity_slots_capacity", "\"Capacity\" > 0");
                    table.CheckConstraint("CK_analysis_capacity_slots_lease_token", "\"LeaseToken\" IS NULL OR \"LeaseToken\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_analysis_capacity_slots_release_reason", "(\"State\" = 'FREE' AND \"ReleasedReason\" IS NOT NULL AND length(\"ReleasedReason\") > 0) OR \"State\" <> 'FREE'");
                    table.CheckConstraint("CK_analysis_capacity_slots_reservation", "(\"State\" = 'FREE' AND \"ReservedRunId\" IS NULL AND \"ReservedJobId\" IS NULL AND \"LeaseToken\" IS NULL AND \"ReservationGeneration\" IS NULL AND \"HardDeadlineUtc\" IS NULL) OR (\"State\" <> 'FREE' AND \"ReservedRunId\" IS NOT NULL AND \"ReservedJobId\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"ReservationGeneration\" IS NOT NULL AND \"HardDeadlineUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_analysis_capacity_slots_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                    table.CheckConstraint("CK_analysis_capacity_slots_slot_number", "\"SlotNumber\" >= 0");
                    table.CheckConstraint("CK_analysis_capacity_slots_state", "\"State\" IN ('FREE', 'RESERVED', 'RELEASING', 'RECOVERING')");
                    table.ForeignKey(
                        name: "FK_analysis_capacity_slots_asset_processing_jobs_ReservedJobId",
                        column: x => x.ReservedJobId,
                        principalTable: "asset_processing_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_report_stages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Applicability = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Retryable = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    InputSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    OutputSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ToolIdentityJson = table.Column<string>(type: "jsonb", nullable: false),
                    CountsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReuseParentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReuseParentStageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_report_stages", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_report_stages_applicability", "\"Applicability\" IN ('MANDATORY', 'OPTIONAL')");
                    table.CheckConstraint("CK_code_analysis_report_stages_counts", "jsonb_typeof(\"CountsJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_report_stages_duration", "\"DurationMilliseconds\" >= 0");
                    table.CheckConstraint("CK_code_analysis_report_stages_input_sha", "\"InputSha256\" IS NULL OR \"InputSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_stages_outcome", "\"Outcome\" IN ('SUCCEEDED', 'FAILED')");
                    table.CheckConstraint("CK_code_analysis_report_stages_output_sha", "\"OutputSha256\" IS NULL OR \"OutputSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_stages_stage", "\"Stage\" ~ '^[A-Z][A-Z0-9_]{0,31}$'");
                    table.CheckConstraint("CK_code_analysis_report_stages_tool_identity", "jsonb_typeof(\"ToolIdentityJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_code_analysis_report_stages_code_analysis_report_headers_Re~",
                        column: x => x.ReportId,
                        principalTable: "code_analysis_report_headers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_report_stages_code_analysis_report_stages_Reu~",
                        column: x => x.ReuseParentStageId,
                        principalTable: "code_analysis_report_stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_reuse_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ArtifactDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    ArtifactJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_reuse_artifacts", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_reuse_artifacts_digest", "\"ArtifactDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_reuse_artifacts_payload", "jsonb_typeof(\"ArtifactJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_reuse_artifacts_size", "\"PayloadBytes\" > 0 AND octet_length(CAST(\"ArtifactJson\" AS text)) <= 262144");
                    table.CheckConstraint("CK_code_analysis_reuse_artifacts_stage", "\"Stage\" ~ '^[A-Z][A-Z0-9_]{0,31}$'");
                    table.ForeignKey(
                        name: "FK_code_analysis_reuse_artifacts_code_analysis_report_headers_~",
                        column: x => x.SourceReportId,
                        principalTable: "code_analysis_report_headers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_reuse_artifacts_code_analysis_report_stages_S~",
                        column: x => x.SourceStageId,
                        principalTable: "code_analysis_report_stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetDraftWorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContentSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    PolicyVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    DeclarationRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeclarationDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    MaterialRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    MaterialDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    InputDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    SelectionGeneration = table.Column<int>(type: "integer", nullable: false),
                    ParentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SecurityRecheckId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchiveEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MalwareEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecurityGeneration = table.Column<int>(type: "integer", nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TransportLeaseToken = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ReservationGeneration = table.Column<int>(type: "integer", nullable: true),
                    HardDeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_runs", x => x.Id);
                    table.UniqueConstraint("AK_code_analysis_runs_AssetId_AssetVersionId_Id", x => new { x.AssetId, x.AssetVersionId, x.Id });
                    table.CheckConstraint("CK_code_analysis_runs_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_declaration_digest", "\"DeclarationDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_disposition", "\"Disposition\" IS NULL OR \"Disposition\" IN ('SELECTED', 'HISTORICAL_SUPERSEDED', 'HISTORICAL_STALE_INPUT', 'HISTORICAL_INCOMPLETE')");
                    table.CheckConstraint("CK_code_analysis_runs_input_digest", "\"InputDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_material_digest", "\"MaterialDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_policy_digest", "\"PolicyDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_reservation", "((\"TransportLeaseToken\" IS NULL AND \"ReservationGeneration\" IS NULL AND \"HardDeadlineUtc\" IS NULL) OR (\"TransportLeaseToken\" IS NOT NULL AND \"ReservationGeneration\" IS NOT NULL AND \"HardDeadlineUtc\" IS NOT NULL))");
                    table.CheckConstraint("CK_code_analysis_runs_security_generation", "\"SecurityGeneration\" > 0");
                    table.CheckConstraint("CK_code_analysis_runs_selection_generation", "\"SelectionGeneration\" > 0");
                    table.CheckConstraint("CK_code_analysis_runs_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                    table.CheckConstraint("CK_code_analysis_runs_state", "\"State\" IN ('QUEUED', 'RUNNING', 'COMPLETED', 'FAILED', 'CANCELLED')");
                    table.CheckConstraint("CK_code_analysis_runs_terminal_completed_at", "(\"State\" IN ('COMPLETED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR (\"State\" NOT IN ('COMPLETED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_code_analysis_runs_transport_lease_token", "\"TransportLeaseToken\" IS NULL OR \"TransportLeaseToken\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_draft_workspaces_AssetDraftWorkspa~",
                        column: x => x.AssetDraftWorkspaceId,
                        principalTable: "asset_draft_workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_material_metadata_revisions_Materi~",
                        column: x => x.MaterialRevisionId,
                        principalTable: "asset_material_metadata_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_processing_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "asset_processing_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_source_declaration_revisions_Decla~",
                        column: x => x.DeclarationRevisionId,
                        principalTable: "asset_source_declaration_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_code_analysis_policy_versions_PolicyVers~",
                        column: x => x.PolicyVersionId,
                        principalTable: "code_analysis_policy_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_code_analysis_runs_ParentRunId",
                        column: x => x.ParentRunId,
                        principalTable: "code_analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_evidence_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    SecurityRecheckId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    DefinitionVersion = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SecurityPolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImplementationIdentity = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EngineIdentity = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EngineVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SignatureIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SignatureObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignatureValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IdentityAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    ScannedByteCount = table.Column<long>(type: "bigint", nullable: false),
                    ScannedEntryCount = table.Column<int>(type: "integer", nullable: true),
                    ScopeComplete = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EvidenceDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_evidence_records", x => x.Id);
                    table.CheckConstraint("CK_security_evidence_records_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_security_evidence_records_digest", "\"EvidenceDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_security_evidence_records_generation", "\"Generation\" > 0");
                    table.CheckConstraint("CK_security_evidence_records_identity", "(\"EvidenceKind\" = 'ARCHIVE_VALIDATION' AND \"ImplementationIdentity\" IS NOT NULL AND \"EngineIdentity\" IS NULL) OR (\"EvidenceKind\" = 'MALWARE_SCAN' AND \"ImplementationIdentity\" IS NULL AND ((\"IdentityAvailable\" AND \"EngineIdentity\" IS NOT NULL) OR (NOT \"IdentityAvailable\" AND \"EngineIdentity\" IS NULL)))");
                    table.CheckConstraint("CK_security_evidence_records_kind", "\"EvidenceKind\" IN ('ARCHIVE_VALIDATION', 'MALWARE_SCAN')");
                    table.CheckConstraint("CK_security_evidence_records_outcome", "\"Outcome\" IN ('VALID', 'CLEAN', 'UNSAFE', 'INFECTED', 'ERROR')");
                    table.CheckConstraint("CK_security_evidence_records_scanned_bytes", "\"ScannedByteCount\" >= 0");
                    table.CheckConstraint("CK_security_evidence_records_signature_scope", "(\"IdentityAvailable\" AND \"SignatureObservedAt\" IS NOT NULL AND \"SignatureValidUntil\" IS NOT NULL) OR (NOT \"IdentityAvailable\" AND \"SignatureObservedAt\" IS NULL AND \"SignatureValidUntil\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_security_evidence_records_asset_processing_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "asset_processing_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_rechecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    SecurityGeneration = table.Column<int>(type: "integer", nullable: false),
                    SecurityPolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "integer", nullable: false),
                    OperationDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    OperationReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SelectedGeneration = table.Column<int>(type: "integer", nullable: true),
                    ParentRecheckId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchiveEvidenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    MalwareEvidenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedGeneration = table.Column<int>(type: "integer", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_rechecks", x => x.Id);
                    table.UniqueConstraint("AK_security_rechecks_AssetId_AssetVersionId_Id", x => new { x.AssetId, x.AssetVersionId, x.Id });
                    table.CheckConstraint("CK_security_rechecks_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_security_rechecks_definition_version", "\"DefinitionVersion\" > 0");
                    table.CheckConstraint("CK_security_rechecks_generation", "\"SecurityGeneration\" > 0");
                    table.CheckConstraint("CK_security_rechecks_operation_digest", "\"OperationDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_security_rechecks_selected_generation", "\"SelectedGeneration\" IS NULL OR \"SelectedGeneration\" = \"SecurityGeneration\"");
                    table.CheckConstraint("CK_security_rechecks_state", "\"State\" IN ('QUEUED', 'INSPECTING', 'SCANNING', 'SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED')");
                    table.CheckConstraint("CK_security_rechecks_terminal_completed_at", "(\"State\" IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR (\"State\" NOT IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_security_rechecks_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_rechecks_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_rechecks_security_evidence_records_ArchiveEvidence~",
                        column: x => x.ArchiveEvidenceId,
                        principalTable: "security_evidence_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_rechecks_security_evidence_records_MalwareEvidence~",
                        column: x => x.MalwareEvidenceId,
                        principalTable: "security_evidence_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_rechecks_security_rechecks_ParentRecheckId",
                        column: x => x.ParentRecheckId,
                        principalTable: "security_rechecks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_report_headers_version",
                table: "code_analysis_report_headers",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_headers_finalized_run",
                table: "code_analysis_report_headers",
                column: "AnalysisRunId",
                unique: true,
                filter: "\"AnalysisRunId\" IS NOT NULL AND \"IsFinalized\"");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_headers_python_report_id",
                table: "code_analysis_report_headers",
                column: "PythonReportId",
                unique: true,
                filter: "\"PythonReportId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_headers_counts",
                table: "code_analysis_report_headers",
                sql: "(\"AnalyzedFileCount\" IS NULL OR \"AnalyzedFileCount\" >= 0) AND (\"OmissionCount\" IS NULL OR \"OmissionCount\" >= 0) AND (\"FindingCount\" IS NULL OR \"FindingCount\" >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_headers_coverage",
                table: "code_analysis_report_headers",
                sql: "\"CoverageLevel\" IS NULL OR \"CoverageLevel\" IN ('FULL', 'PARTIAL', 'UNSUPPORTED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_headers_disposition",
                table: "code_analysis_report_headers",
                sql: "\"Disposition\" IS NULL OR \"Disposition\" IN ('SELECTED', 'HISTORICAL_SUPERSEDED', 'HISTORICAL_STALE_INPUT', 'HISTORICAL_INCOMPLETE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_headers_python_report_id",
                table: "code_analysis_report_headers",
                sql: "\"PythonReportId\" IS NULL OR \"PythonReportId\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_headers_report_digest",
                table: "code_analysis_report_headers",
                sql: "\"ReportDigest\" IS NULL OR \"ReportDigest\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_headers_selection_generation",
                table: "code_analysis_report_headers",
                sql: "\"SelectionGeneration\" IS NULL OR \"SelectionGeneration\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_asset_versions_SelectedCodeAnalysisReportId",
                table: "asset_versions",
                column: "SelectedCodeAnalysisReportId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_versions_analysis_selection_generation",
                table: "asset_versions",
                sql: "\"AnalysisSelectionGeneration\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_versions_safety_restriction",
                table: "asset_versions",
                sql: "\"SafetyRestriction\" IN ('NONE', 'UNSAFE_ARCHIVE', 'INFECTED', 'INTEGRITY_MISMATCH')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_versions_safety_restriction_reason",
                table: "asset_versions",
                sql: "(\"SafetyRestriction\" = 'NONE' AND \"SafetyRestrictionReason\" IS NULL AND \"SafetyRestrictedAt\" IS NULL) OR (\"SafetyRestriction\" <> 'NONE' AND \"SafetyRestrictionReason\" IS NOT NULL AND \"SafetyRestrictedAt\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_AssetId_AssetVersionId_AnalysisRunId",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId", "AnalysisRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_AssetId_AssetVersionId_SecurityRechec~",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId", "SecurityRecheckId" });

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "AssetVersionId", "Type", "DefinitionVersion" },
                unique: true,
                filter: "\"Type\" NOT IN ('EMBEDDING_GENERATION', 'CODE_ANALYSIS') AND \"SecurityRecheckId\" IS NULL AND \"AnalysisRunId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_recheck_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "SecurityRecheckId", "Type", "DefinitionVersion" },
                unique: true,
                filter: "\"SecurityRecheckId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_run_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "AnalysisRunId", "Type", "DefinitionVersion" },
                unique: true,
                filter: "\"AnalysisRunId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_processing_jobs_scope_exclusivity",
                table: "asset_processing_jobs",
                sql: "\"Type\" NOT IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT', 'EMBEDDING_GENERATION', 'CODE_ANALYSIS') OR ((\"Type\" = 'CODE_ANALYSIS' AND \"AnalysisRunId\" IS NOT NULL AND \"SecurityRecheckId\" IS NULL) OR (\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN') AND \"AnalysisRunId\" IS NULL) OR (\"Type\" IN ('LISTING_COPILOT', 'EMBEDDING_GENERATION') AND \"AnalysisRunId\" IS NULL AND \"SecurityRecheckId\" IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_processing_jobs_transport_lease_token",
                table: "asset_processing_jobs",
                sql: "\"TransportLeaseToken\" IS NULL OR \"TransportLeaseToken\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_processing_jobs_type",
                table: "asset_processing_jobs",
                sql: "\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT', 'EMBEDDING_GENERATION', 'CODE_ANALYSIS')");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_hard_deadline",
                table: "analysis_capacity_slots",
                column: "HardDeadlineUtc");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_ReservedJobId",
                table: "analysis_capacity_slots",
                column: "ReservedJobId");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_ReservedRunId",
                table: "analysis_capacity_slots",
                column: "ReservedRunId");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_service",
                table: "analysis_capacity_slots",
                column: "ServiceKey");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_service_state",
                table: "analysis_capacity_slots",
                columns: new[] { "ServiceKey", "State" });

            migrationBuilder.CreateIndex(
                name: "UIX_analysis_capacity_slots_service_slot",
                table: "analysis_capacity_slots",
                columns: new[] { "ServiceKey", "SlotNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_analysis_service_registrations_service_key",
                table: "analysis_service_registrations",
                column: "ServiceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_policy_versions_identity",
                table: "code_analysis_policy_versions",
                columns: new[] { "PolicyKey", "PolicyDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_admissions_report",
                table: "code_analysis_report_admissions",
                column: "ReportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_findings_report_key",
                table: "code_analysis_report_findings",
                columns: new[] { "ReportId", "FindingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_report_stages_ReuseParentRunId",
                table: "code_analysis_report_stages",
                column: "ReuseParentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_report_stages_ReuseParentStageId",
                table: "code_analysis_report_stages",
                column: "ReuseParentStageId");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_stages_report_stage",
                table: "code_analysis_report_stages",
                columns: new[] { "ReportId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_reuse_artifacts_digest",
                table: "code_analysis_reuse_artifacts",
                columns: new[] { "Stage", "ArtifactDigest" });

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_reuse_artifacts_SourceReportId",
                table: "code_analysis_reuse_artifacts",
                column: "SourceReportId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_reuse_artifacts_SourceStageId",
                table: "code_analysis_reuse_artifacts",
                column: "SourceStageId");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_reuse_artifacts_run_stage",
                table: "code_analysis_reuse_artifacts",
                columns: new[] { "SourceRunId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_ArchiveEvidenceId",
                table: "code_analysis_runs",
                column: "ArchiveEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_AssetDraftWorkspaceId",
                table: "code_analysis_runs",
                column: "AssetDraftWorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_DeclarationRevisionId",
                table: "code_analysis_runs",
                column: "DeclarationRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_MalwareEvidenceId",
                table: "code_analysis_runs",
                column: "MalwareEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_MaterialRevisionId",
                table: "code_analysis_runs",
                column: "MaterialRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_ParentRunId",
                table: "code_analysis_runs",
                column: "ParentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_PolicyVersionId",
                table: "code_analysis_runs",
                column: "PolicyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_SecurityRecheckId",
                table: "code_analysis_runs",
                column: "SecurityRecheckId");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_active_generation",
                table: "code_analysis_runs",
                columns: new[] { "AssetVersionId", "SelectionGeneration" },
                unique: true,
                filter: "\"State\" IN ('QUEUED', 'RUNNING')");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_active_input",
                table: "code_analysis_runs",
                columns: new[] { "AssetId", "AssetVersionId", "ContentSha256", "PolicyDigest", "DeclarationDigest", "MaterialDigest", "SelectionGeneration", "SecurityGeneration", "ArchiveEvidenceId", "MalwareEvidenceId" },
                unique: true,
                filter: "\"State\" IN ('QUEUED', 'RUNNING')");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_input_digest",
                table: "code_analysis_runs",
                column: "InputDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_job",
                table: "code_analysis_runs",
                column: "JobId",
                unique: true,
                filter: "\"JobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_scope",
                table: "code_analysis_runs",
                columns: new[] { "AssetId", "AssetVersionId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_evidence_records_JobId",
                table: "security_evidence_records",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "UIX_security_evidence_records_digest",
                table: "security_evidence_records",
                column: "EvidenceDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_security_evidence_records_recheck_kind",
                table: "security_evidence_records",
                columns: new[] { "SecurityRecheckId", "EvidenceKind" },
                unique: true,
                filter: "\"SecurityRecheckId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_security_evidence_records_upload_kind",
                table: "security_evidence_records",
                columns: new[] { "AssetVersionId", "EvidenceKind" },
                unique: true,
                filter: "\"SecurityRecheckId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_security_rechecks_ArchiveEvidenceId",
                table: "security_rechecks",
                column: "ArchiveEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_security_rechecks_MalwareEvidenceId",
                table: "security_rechecks",
                column: "MalwareEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_security_rechecks_ParentRecheckId",
                table: "security_rechecks",
                column: "ParentRecheckId");

            migrationBuilder.CreateIndex(
                name: "UIX_security_rechecks_generation",
                table: "security_rechecks",
                columns: new[] { "AssetVersionId", "SecurityGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_security_rechecks_operation_receipt",
                table: "security_rechecks",
                columns: new[] { "AssetVersionId", "OperationReceiptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_security_rechecks_scope",
                table: "security_rechecks",
                columns: new[] { "AssetId", "AssetVersionId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_security_rechecks_selected",
                table: "security_rechecks",
                column: "AssetVersionId",
                unique: true,
                filter: "\"SelectedGeneration\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_asset_processing_jobs_code_analysis_runs_AssetId_AssetVersi~",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId", "AnalysisRunId" },
                principalTable: "code_analysis_runs",
                principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_processing_jobs_security_rechecks_AssetId_AssetVersio~",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId", "SecurityRecheckId" },
                principalTable: "security_rechecks",
                principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_versions_code_analysis_report_headers_SelectedCodeAna~",
                table: "asset_versions",
                column: "SelectedCodeAnalysisReportId",
                principalTable: "code_analysis_report_headers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_report_headers_code_analysis_runs_AnalysisRun~",
                table: "code_analysis_report_headers",
                column: "AnalysisRunId",
                principalTable: "code_analysis_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_analysis_capacity_slots_code_analysis_runs_ReservedRunId",
                table: "analysis_capacity_slots",
                column: "ReservedRunId",
                principalTable: "code_analysis_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_report_stages_code_analysis_runs_ReuseParentR~",
                table: "code_analysis_report_stages",
                column: "ReuseParentRunId",
                principalTable: "code_analysis_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_reuse_artifacts_code_analysis_runs_SourceRunId",
                table: "code_analysis_reuse_artifacts",
                column: "SourceRunId",
                principalTable: "code_analysis_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_runs_security_evidence_records_ArchiveEvidenc~",
                table: "code_analysis_runs",
                column: "ArchiveEvidenceId",
                principalTable: "security_evidence_records",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_runs_security_evidence_records_MalwareEvidenc~",
                table: "code_analysis_runs",
                column: "MalwareEvidenceId",
                principalTable: "security_evidence_records",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_runs_security_rechecks_SecurityRecheckId",
                table: "code_analysis_runs",
                column: "SecurityRecheckId",
                principalTable: "security_rechecks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_security_evidence_records_security_rechecks_SecurityRecheck~",
                table: "security_evidence_records",
                column: "SecurityRecheckId",
                principalTable: "security_rechecks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_asset_processing_jobs_code_analysis_runs_AssetId_AssetVersi~",
                table: "asset_processing_jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_asset_processing_jobs_security_rechecks_AssetId_AssetVersio~",
                table: "asset_processing_jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_asset_versions_code_analysis_report_headers_SelectedCodeAna~",
                table: "asset_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_code_analysis_report_headers_code_analysis_runs_AnalysisRun~",
                table: "code_analysis_report_headers");

            migrationBuilder.DropForeignKey(
                name: "FK_security_rechecks_security_evidence_records_ArchiveEvidence~",
                table: "security_rechecks");

            migrationBuilder.DropForeignKey(
                name: "FK_security_rechecks_security_evidence_records_MalwareEvidence~",
                table: "security_rechecks");

            migrationBuilder.DropTable(
                name: "analysis_capacity_slots");

            migrationBuilder.DropTable(
                name: "analysis_service_registrations");

            migrationBuilder.DropTable(
                name: "code_analysis_report_admissions");

            migrationBuilder.DropTable(
                name: "code_analysis_report_findings");

            migrationBuilder.DropTable(
                name: "code_analysis_reuse_artifacts");

            migrationBuilder.DropTable(
                name: "code_analysis_report_stages");

            migrationBuilder.DropTable(
                name: "code_analysis_runs");

            migrationBuilder.DropTable(
                name: "code_analysis_policy_versions");

            migrationBuilder.DropTable(
                name: "security_evidence_records");

            migrationBuilder.DropTable(
                name: "security_rechecks");

            migrationBuilder.DropIndex(
                name: "IX_code_analysis_report_headers_version",
                table: "code_analysis_report_headers");

            migrationBuilder.DropIndex(
                name: "UIX_code_analysis_report_headers_finalized_run",
                table: "code_analysis_report_headers");

            migrationBuilder.DropIndex(
                name: "UIX_code_analysis_report_headers_python_report_id",
                table: "code_analysis_report_headers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_headers_counts",
                table: "code_analysis_report_headers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_headers_coverage",
                table: "code_analysis_report_headers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_headers_disposition",
                table: "code_analysis_report_headers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_headers_python_report_id",
                table: "code_analysis_report_headers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_headers_report_digest",
                table: "code_analysis_report_headers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_headers_selection_generation",
                table: "code_analysis_report_headers");

            migrationBuilder.DropIndex(
                name: "IX_asset_versions_SelectedCodeAnalysisReportId",
                table: "asset_versions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_versions_analysis_selection_generation",
                table: "asset_versions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_versions_safety_restriction",
                table: "asset_versions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_versions_safety_restriction_reason",
                table: "asset_versions");

            migrationBuilder.DropIndex(
                name: "IX_asset_processing_jobs_AssetId_AssetVersionId_AnalysisRunId",
                table: "asset_processing_jobs");

            migrationBuilder.DropIndex(
                name: "IX_asset_processing_jobs_AssetId_AssetVersionId_SecurityRechec~",
                table: "asset_processing_jobs");

            migrationBuilder.DropIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs");

            migrationBuilder.DropIndex(
                name: "UIX_asset_processing_jobs_recheck_idempotency",
                table: "asset_processing_jobs");

            migrationBuilder.DropIndex(
                name: "UIX_asset_processing_jobs_run_idempotency",
                table: "asset_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_processing_jobs_scope_exclusivity",
                table: "asset_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_processing_jobs_transport_lease_token",
                table: "asset_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_processing_jobs_type",
                table: "asset_processing_jobs");

            migrationBuilder.DropColumn(
                name: "AnalysisRunId",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "AnalyzedFileCount",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "CoverageLevel",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "Disposition",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "FindingCount",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "OmissionCount",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "PythonReportId",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "ReportDigest",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "SelectionGeneration",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "AnalysisSelectionGeneration",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "SafetyRestrictedAt",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "SafetyRestriction",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "SafetyRestrictionReason",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "SelectedCodeAnalysisReportId",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "AnalysisRunId",
                table: "asset_processing_jobs");

            migrationBuilder.DropColumn(
                name: "SecurityRecheckId",
                table: "asset_processing_jobs");

            migrationBuilder.DropColumn(
                name: "TransportLeaseToken",
                table: "asset_processing_jobs");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_report_headers_AssetId_AssetVersionId_Content~",
                table: "code_analysis_report_headers",
                columns: new[] { "AssetId", "AssetVersionId", "ContentSha256", "PolicyVersion", "InputRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "AssetVersionId", "Type", "DefinitionVersion" },
                unique: true,
                filter: "\"Type\" <> 'EMBEDDING_GENERATION'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_processing_jobs_type",
                table: "asset_processing_jobs",
                sql: "\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT', 'EMBEDDING_GENERATION')");
        }
    }
}
