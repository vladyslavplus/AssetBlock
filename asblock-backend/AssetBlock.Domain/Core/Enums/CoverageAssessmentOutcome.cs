using System.Text.Json.Serialization;
namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter<CoverageAssessmentOutcome>))]
public enum CoverageAssessmentOutcome { ACCEPTED, BLOCKED }
