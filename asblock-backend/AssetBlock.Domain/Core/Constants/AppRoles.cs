namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Application role names. Use with JWT generation.
/// </summary>
public static class AppRoles
{
    public const string ADMIN = "Admin";
    public const string MODERATOR = "Moderator";
    public const string USER = "User";

    /// <summary>Roles assignable through the scoped Admin User/Moderator endpoint.</summary>
    public static readonly IReadOnlySet<string> AdminAssignableRoles =
        new HashSet<string>(StringComparer.Ordinal) { USER, MODERATOR };
}
