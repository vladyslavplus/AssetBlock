namespace AssetBlock.WebApi.Configuration;

/// <summary>
/// Browser CORS policy for SPA clients (e.g. Next.js). Configure <c>Cors:AllowedOrigins</c> in appsettings.
/// </summary>
public sealed class CorsOptions
{
    public const string SECTION_NAME = "Cors";

    /// <summary>Allowed origins without trailing slash (e.g. http://localhost:3000).</summary>
    public string[] AllowedOrigins { get; set; } = [];
}
