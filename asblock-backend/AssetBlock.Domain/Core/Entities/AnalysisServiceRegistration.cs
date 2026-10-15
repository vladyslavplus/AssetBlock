using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Registration row that serializes capacity initialization for one analysis service identity.
/// Created once per service key; its capacity is immutable afterwards so concurrent workers cannot
/// converge on a mixed slot set.
/// </summary>
public class AnalysisServiceRegistration : BaseEntity
{
    public required string ServiceKey { get; set; }

    public required int Capacity { get; set; }

    public DateTimeOffset? RegisteredAt { get; set; }
}
