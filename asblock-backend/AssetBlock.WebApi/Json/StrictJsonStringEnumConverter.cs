using System.Text.Json.Serialization;

namespace AssetBlock.WebApi.Json;

/// <summary>
/// String-only enum converter for request inputs whose default member must never bind implicitly.
/// Numeric tokens are rejected, so a missing or numeric value cannot silently select a member.
/// </summary>
internal sealed class StrictJsonStringEnumConverter() : JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false);
