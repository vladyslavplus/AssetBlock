using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPgvectorAssetEmbeddingStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asset_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelKey = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ModelRevision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ModelDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    ContentSchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(768)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_embeddings", x => x.Id);
                    table.CheckConstraint("CK_asset_embeddings_content_hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_asset_embeddings_dimension", "\"Dimension\" = 768");
                    table.CheckConstraint("CK_asset_embeddings_model_digest", "\"ModelDigest\" ~ '^sha256:[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_asset_embeddings_model_key", "\"ModelKey\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_asset_embeddings_source_revision", "\"SourceRevision\" > 0");
                    table.CheckConstraint("CK_asset_embeddings_vector_dims", "vector_dims(\"Embedding\") = \"Dimension\"");
                    table.ForeignKey(
                        name: "FK_asset_embeddings_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_embeddings_model_key_asset_id",
                table: "asset_embeddings",
                columns: new[] { "ModelKey", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "UIX_asset_embeddings_asset_id_model_key",
                table: "asset_embeddings",
                columns: new[] { "AssetId", "ModelKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_embeddings");
        }
    }
}
