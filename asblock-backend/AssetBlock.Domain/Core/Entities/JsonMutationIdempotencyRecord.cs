using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class JsonMutationIdempotencyRecord : BaseEntity
{
    public required Guid ActorUserId { get; init; }
    public required string OperationKind { get; init; }
    public required Guid OperationId { get; init; }
    public required string RequestDigest { get; init; }
    public required string ResultJson { get; init; }

    public User ActorUser { get; set; } = null!;
}
