namespace AssetBlock.Domain.Core.Constants;

/// <summary>Bounded input limits for private seller drafts and source declarations.</summary>
public static class SellerDraftLimits
{
    public const int TITLE_MAX_LENGTH = 500;
    public const int DESCRIPTION_MAX_LENGTH = 5000;
    public const int RELEASE_NOTES_MAX_LENGTH = 4000;
    public const int COMPONENTS_MAX_COUNT = 100;
    public const int EXPLANATION_MAX_LENGTH = 4000;
    public const int EVIDENCE_REFERENCES_MAX_COUNT = 20;
    public const int NOTICE_LOCATIONS_MAX_COUNT = 20;

    // Component field limits; the frontend Zod schemas mirror these exactly.
    public const int COMPONENT_ID_MAX_LENGTH = 100;
    public const int COMPONENT_NAME_MAX_LENGTH = 200;
    public const int COMPONENT_PATH_MAX_LENGTH = 200;
    public const int COMPONENT_SOURCE_URL_MAX_LENGTH = 2000;
    public const int COMPONENT_KNOWN_VERSION_MAX_LENGTH = 200;
    public const int COMPONENT_LICENSE_MAX_LENGTH = 200;
    public const int COMPONENT_NOTICE_ITEM_MAX_LENGTH = 500;
    public const int COMPONENT_REF_ITEM_MAX_LENGTH = 500;

    /// <summary>Aggregate declaration input limit in UTF-8 bytes. Oversize requests are rejected, never truncated.</summary>
    public const int DECLARATION_MAX_BYTES = 256 * 1024;

    /// <summary>Explicit marker stored when the seller has not determined a component license.</summary>
    public const string LICENSE_UNKNOWN = "UNKNOWN";

    public const string DECLARATION_DISCLOSURE_POLICY_VERSION = "disclosure-v1";
    public const int DECLARATION_SCHEMA_VERSION = 1;
    public const int MATERIAL_METADATA_SCHEMA_VERSION = 1;
}
