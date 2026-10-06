using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Publication;

/// <summary>Approved-metadata lexical scoring for public catalog search (no mutable re-filters).</summary>
internal static class ApprovedCatalogLexicalSearch
{
    internal static IQueryable<ScoredAssetRow> ScoreCandidates(
        IQueryable<Asset> searchableBase,
        string searchText,
        string exactTitlePattern,
        string likePattern,
        bool isLongEnoughForTrigram,
        string likeEscape)
    {
        return searchableBase
            .Where(a => a.CurrentPublicationSnapshot != null)
            .Select(a => new ScoredAssetRow
            {
                Id = a.Id,
                CreatedAt = a.CreatedAt,
                Score =
                    (EF.Functions.ILike(
                        PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "title") ?? string.Empty,
                        exactTitlePattern,
                        likeEscape)
                        ? 80.0f
                        : 0.0f)
                    + (EF.Functions.ILike(
                        PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "title") ?? string.Empty,
                        likePattern,
                        likeEscape)
                        ? 40.0f
                        : 0.0f)
                    + (PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "description") != null
                        && EF.Functions.ILike(
                            PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "description")!,
                            likePattern,
                            likeEscape)
                        ? 15.0f
                        : 0.0f)
                    + (isLongEnoughForTrigram
                        && EF.Functions.TrigramsAreSimilar(
                            PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "title") ?? string.Empty,
                            searchText)
                        && PostgresDbFunctions.TrigramsSimilarity(
                            PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "title") ?? string.Empty,
                            searchText) >= AssetStoreLexicalConstants.TRIGRAM_SIMILARITY_THRESHOLD
                        ? PostgresDbFunctions.TrigramsSimilarity(
                            PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "title") ?? string.Empty,
                            searchText) * 20.0f
                        : 0.0f)
                    + (isLongEnoughForTrigram
                        && PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "description") != null
                        && EF.Functions.TrigramsAreSimilar(
                            PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "description")!,
                            searchText)
                        && PostgresDbFunctions.TrigramsSimilarity(
                            PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "description")!,
                            searchText) >= AssetStoreLexicalConstants.TRIGRAM_SIMILARITY_THRESHOLD
                        ? PostgresDbFunctions.TrigramsSimilarity(
                            PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot!.ApprovedMetadataJson, "description")!,
                            searchText) * 5.0f
                        : 0.0f)
            });
    }

    internal static IQueryable<Asset> BuildSearchableBase(
        IQueryable<Asset> filteredBase,
        string searchText,
        string likePattern,
        bool isLongEnoughForTrigram,
        string likeEscape) =>
        filteredBase.Where(a =>
            a.CurrentPublicationSnapshot != null
            && (EF.Functions.ILike(
                    PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot.ApprovedMetadataJson, "title") ?? string.Empty,
                    likePattern,
                    likeEscape)
                || (PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot.ApprovedMetadataJson, "description") != null
                    && EF.Functions.ILike(
                        PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot.ApprovedMetadataJson, "description")!,
                        likePattern,
                        likeEscape))
                || (isLongEnoughForTrigram
                    && EF.Functions.TrigramsAreSimilar(
                        PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot.ApprovedMetadataJson, "title") ?? string.Empty,
                        searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(
                        PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot.ApprovedMetadataJson, "title") ?? string.Empty,
                        searchText) >= AssetStoreLexicalConstants.TRIGRAM_SIMILARITY_THRESHOLD)
                || (isLongEnoughForTrigram
                    && PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot.ApprovedMetadataJson, "description") != null
                    && EF.Functions.TrigramsAreSimilar(
                        PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot.ApprovedMetadataJson, "description")!,
                        searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(
                        PostgresDbFunctions.JsonbExtractPathText(a.CurrentPublicationSnapshot.ApprovedMetadataJson, "description")!,
                        searchText) >= AssetStoreLexicalConstants.TRIGRAM_SIMILARITY_THRESHOLD)));

    internal sealed class ScoredAssetRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public float Score { get; init; }
    }
}

/// <summary>Shared lexical constants referenced from EF-translatable search queries.</summary>
internal static class AssetStoreLexicalConstants
{
    internal const float TRIGRAM_SIMILARITY_THRESHOLD = 0.30f;
}
