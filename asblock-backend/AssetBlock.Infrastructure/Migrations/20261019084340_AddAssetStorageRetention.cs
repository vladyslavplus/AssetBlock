using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetStorageRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StorageRetiredAt",
                table: "asset_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageVersionId",
                table: "asset_versions",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "storage_orphan_cleanup_states",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    CursorKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CursorVersionId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_orphan_cleanup_states", x => x.Id);
                    table.CheckConstraint("CK_storage_orphan_cleanup_states_SingletonId", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_orphan_cleanup_states");

            migrationBuilder.DropColumn(
                name: "StorageRetiredAt",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "StorageVersionId",
                table: "asset_versions");
        }
    }
}
