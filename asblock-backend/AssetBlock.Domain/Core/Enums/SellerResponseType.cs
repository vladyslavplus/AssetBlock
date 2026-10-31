using System.Text.Json.Serialization;
namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SellerResponseType { PRIOR_WORK, PERMITTED_COMPONENT, INDEPENDENT_IMPLEMENTATION, WRONG_SOURCE, CORRECTED_FILES, NO_EVIDENCE }
