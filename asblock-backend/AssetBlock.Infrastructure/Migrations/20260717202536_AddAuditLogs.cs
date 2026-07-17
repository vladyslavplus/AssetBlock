using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                    table.CheckConstraint("CK_audit_logs_ActorType", "\"ActorType\" IN ('USER', 'SYSTEM', 'ANONYMOUS')");
                    table.CheckConstraint("CK_audit_logs_MetadataJson_Object", "\"MetadataJson\" IS NULL OR jsonb_typeof(\"MetadataJson\") = 'object'");
                    table.CheckConstraint("CK_audit_logs_Outcome", "\"Outcome\" IN ('SUCCESS', 'FAILURE', 'DENIED')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_Action_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "Action", "OccurredAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_ActorUserId_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "ActorUserId", "OccurredAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "OccurredAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_Outcome_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "Outcome", "OccurredAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_ResourceType_ResourceId_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "ResourceType", "ResourceId", "OccurredAt", "Id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");
        }
    }
}
