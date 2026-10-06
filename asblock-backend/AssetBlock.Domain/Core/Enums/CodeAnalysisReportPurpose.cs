namespace AssetBlock.Domain.Core.Enums;

/// <summary>Distinguishes trusted production reports from isolated test/fixture headers.</summary>
public enum CodeAnalysisReportPurpose
{
    PRODUCTION,
    FIXTURE,
    DISABLED
}
