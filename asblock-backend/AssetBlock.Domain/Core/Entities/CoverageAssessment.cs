using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;
namespace AssetBlock.Domain.Core.Entities;
/// <summary>Immutable human prerequisite assessment; never a publication decision.</summary>
public sealed class CoverageAssessment : BaseEntity
{
    public required Guid RequestId { get; init; }
    public required Guid ModeratorUserId { get; init; }
    public required CoverageAssessmentOutcome Outcome { get; init; }
    public required string CompetenceJson { get; init; }
    public required string ScopeJson { get; init; }
    public required bool ManageableScope { get; init; }
    public required string ReviewPlan { get; init; }
    public required string CompletedPlanReference { get; init; }
    public required string Rationale { get; init; }
}
