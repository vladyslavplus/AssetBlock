namespace AssetBlock.Infrastructure.Persistence;

/// <summary>
/// Maps to PostgreSQL functions for EF Core translation. Do not call from CLR.
/// </summary>
internal static class PostgresDbFunctions
{
    /// <summary>Maps to pg_trgm similarity(text, text) which returns real.</summary>
    public static float TrigramsSimilarity(string a, string b)
        => throw new NotSupportedException("TrigramsSimilarity is for EF Core SQL translation only.");

    /// <summary>Maps to jsonb_extract_path_text(jsonb, text).</summary>
    public static string? JsonbExtractPathText(string json, string path)
        => throw new NotSupportedException("JsonbExtractPathText is for EF Core SQL translation only.");
}
