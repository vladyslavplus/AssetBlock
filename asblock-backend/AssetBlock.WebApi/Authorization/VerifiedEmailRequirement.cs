using Microsoft.AspNetCore.Authorization;

namespace AssetBlock.WebApi.Authorization;

/// <summary>
/// Requires the caller's EmailVerifiedAt to be set in the database (not a JWT claim).
/// </summary>
public sealed class VerifiedEmailRequirement : IAuthorizationRequirement;
