using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Core.Dto;

/// <summary>Request to start (or resolve) a security recheck for an already READY version.</summary>
public sealed record SecurityRecheckStartRequest(
    Guid AssetId,
    Guid AssetVersionId,
    string ContentSha256,
    string SecurityPolicyVersion,
    int DefinitionVersion,
    string OperationDigest,
    Guid OperationReceiptId,
    Guid? ParentRecheckId = null);

/// <summary>Outcome of starting a recheck, including whether an identical request was replayed.</summary>
public sealed record SecurityRecheckStartResult(
    Guid RecheckId,
    int SecurityGeneration,
    Guid ArchiveJobId,
    bool IsReplay);

/// <summary>Result of a leased recheck finalizer transition.</summary>
public sealed record SecurityRecheckTransitionResult(
    bool Committed,
    bool IsReplay,
    Guid? AnalysisRunId = null,
    Guid? AnalysisJobId = null,
    Guid? MalwareJobId = null);
