namespace AssetBlock.WebApi.IntegrationTests.Support;

/// <summary>Route prefixes that the shared ApiRoutes constants do not carry.</summary>
internal static class IntegrationTestRoutes
{
    // ApiRoutes.Payments has no base segment. The checkout route is mounted under this prefix.
    public const string PAYMENTS_BASE = "/api/payments";
}
