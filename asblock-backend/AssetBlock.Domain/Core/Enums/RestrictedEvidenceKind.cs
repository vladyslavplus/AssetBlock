using System.Text.Json.Serialization;
namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter<RestrictedEvidenceKind>))]
public enum RestrictedEvidenceKind { OWN_EXCERPT, FINDING_PROVENANCE, DECLARATION, MATERIAL_METADATA, SELLER_EVIDENCE, COVERAGE_INVENTORY, ARCHIVE_README }
