namespace AssetBlock.Domain.Core.Enums;

/// <summary>Current email-action purpose stored as stable string in PostgreSQL.</summary>
public enum EmailActionPurpose
{
    EMAIL_VERIFICATION,
    PASSWORD_RESET,
    EMAIL_CHANGE
}
