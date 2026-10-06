namespace AssetBlock.Domain.Core.Constants;

/// <summary>Non-null workspace version-scope keys. Pre-upload workspaces use the reserved sentinel.</summary>
public static class DraftWorkspaceVersionScopes
{
    public static readonly Guid PreUpload = Guid.Empty;
}
