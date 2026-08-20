namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Lean read of email verification state for authorization. Does not load the full user graph.
/// </summary>
public interface IUserVerificationStore
{
    Task<bool> IsEmailVerified(Guid userId, CancellationToken cancellationToken = default);
}
