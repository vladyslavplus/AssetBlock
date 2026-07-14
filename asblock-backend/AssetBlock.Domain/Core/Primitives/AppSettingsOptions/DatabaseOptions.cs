namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

/// <summary>
/// Database startup options: auto-apply migrations and/or ensure database created.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SECTION_NAME = "Database";

    public bool AutoMigrate { get; set; }
    public bool EnsureCreated { get; set; }

    /// <summary>
    /// When true (typically Development only), seeds ~12 demo catalog assets if the Assets table is empty.
    /// Requires Elasticsearch so list/search works; MinIO objects are not created (placeholder storage keys only).
    /// </summary>
    public bool SeedDemoAssets { get; set; }
}
