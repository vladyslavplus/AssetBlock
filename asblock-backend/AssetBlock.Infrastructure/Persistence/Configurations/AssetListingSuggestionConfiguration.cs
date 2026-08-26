using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetListingSuggestionConfiguration : IEntityTypeConfiguration<AssetListingSuggestion>
{
    public const string PRIMARY_KEY = "PK_asset_listing_suggestions";
    public const string CK_TAGS_TYPE = "CK_asset_listing_suggestions_tags_type";
    public const string CK_TAGS_LENGTH = "CK_asset_listing_suggestions_tags_length";
    public const string CK_TAGS_ITEMS = "CK_asset_listing_suggestions_tags_items";
    public const string CK_TAGS_SIZE = "CK_asset_listing_suggestions_tags_size";

    public static readonly string SqlTagsType = "jsonb_typeof(\"Tags\") = 'array'";
    public static readonly string SqlTagsLength = $"jsonb_array_length(\"Tags\") <= {ListingSuggestionBounds.MAX_SUGGESTED_TAGS}";
    public static readonly string SqlTagsItems = """NOT jsonb_path_exists("Tags", '$[*] ? (@.type() != "string")')""";
    public static readonly string SqlTagsSize = $"octet_length(CAST(\"Tags\" AS text)) <= {ListingSuggestionBounds.TAGS_JSON_MAX_BYTES}";

    public void Configure(EntityTypeBuilder<AssetListingSuggestion> builder)
    {
        builder.ToTable("asset_listing_suggestions", table =>
        {
            table.HasCheckConstraint(
                "CK_asset_listing_suggestions_provider",
                "\"Provider\" IN ('OPENROUTER', 'OLLAMA')");
            table.HasCheckConstraint(
                "CK_asset_listing_suggestions_content_hash",
                "\"ContentHash\" ~ '^[a-f0-9]{64}$'");
            table.HasCheckConstraint(CK_TAGS_TYPE, SqlTagsType);
            table.HasCheckConstraint(CK_TAGS_LENGTH, SqlTagsLength);
            table.HasCheckConstraint(CK_TAGS_ITEMS, SqlTagsItems);
            table.HasCheckConstraint(CK_TAGS_SIZE, SqlTagsSize);
            table.HasCheckConstraint(
                "CK_asset_listing_suggestions_input_tokens",
                "\"InputTokens\" IS NULL OR \"InputTokens\" >= 0");
            table.HasCheckConstraint(
                "CK_asset_listing_suggestions_output_tokens",
                "\"OutputTokens\" IS NULL OR \"OutputTokens\" >= 0");
        });

        builder.HasKey(s => s.JobId).HasName(PRIMARY_KEY);

        builder.Property(s => s.PromptPolicyVersion).IsRequired().HasMaxLength(64);
        builder.Property(s => s.Provider)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion(
                p => p.ToString(),
                s => Enum.Parse<AiProviderKind>(s));
        builder.Property(s => s.ModelId).IsRequired().HasMaxLength(ListingSuggestionBounds.MODEL_ID_MAX_LENGTH);
        builder.Property(s => s.ModelRevision).HasMaxLength(ListingSuggestionBounds.MODEL_REVISION_MAX_LENGTH);
        builder.Property(s => s.UpstreamProvider).HasMaxLength(ListingSuggestionBounds.UPSTREAM_PROVIDER_MAX_LENGTH);
        builder.Property(s => s.ProviderRequestId).HasMaxLength(ListingSuggestionBounds.REQUEST_ID_MAX_LENGTH);
        builder.Property(s => s.Title).IsRequired().HasMaxLength(ListingSuggestionBounds.TITLE_MAX_LENGTH);
        builder.Property(s => s.Description).IsRequired().HasMaxLength(ListingSuggestionBounds.DESCRIPTION_MAX_LENGTH);
        builder.Property(s => s.Category).IsRequired().HasMaxLength(ListingSuggestionBounds.CATEGORY_NAME_MAX_LENGTH);
        builder.Property(s => s.Tags).HasColumnType("jsonb").IsRequired();
        builder.Property(s => s.ContentHash)
            .IsRequired()
            .HasColumnType("char(64)")
            .IsFixedLength();
        builder.Property(s => s.InputTokens);
        builder.Property(s => s.OutputTokens);
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasOne(s => s.Job)
            .WithOne()
            .HasForeignKey<AssetListingSuggestion>(s => s.JobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
