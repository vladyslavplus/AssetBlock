namespace AssetBlock.Domain.Core.Dto;

/// <summary>
/// Optional scope that binds a processing job to one analysis run or one security recheck.
/// Exactly one scope may be set, and each is exclusive to its own job types.
/// </summary>
public sealed record AssetProcessingJobScope(
    Guid? AnalysisRunId = null,
    Guid? SecurityRecheckId = null)
{
    public static AssetProcessingJobScope ForRun(Guid analysisRunId) => new(AnalysisRunId: analysisRunId);

    public static AssetProcessingJobScope ForRecheck(Guid securityRecheckId) => new(SecurityRecheckId: securityRecheckId);
}
