namespace AssetBlock.Domain.Primitives.AppSettingsOptions;

/// <summary>
/// Database startup options: auto-apply migrations and/or ensure database created.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SECTION_NAME = "Database";

    public bool AutoMigrate { get; set; }
    public bool EnsureCreated { get; set; }
}
