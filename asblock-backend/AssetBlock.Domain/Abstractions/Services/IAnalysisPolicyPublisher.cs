using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Publishes the approved analysis policy and runtime manifest as one immutable, content-addressed
/// row. Publication is insert-only and idempotent: republishing an identical manifest succeeds
/// without writing, while any attempt to give one policy key a second approved digest, or to change
/// the content behind an existing (key, digest) pair, is refused as a conflict.
/// </summary>
public interface IAnalysisPolicyPublisher
{
    Task<AnalysisPolicyPublishResult> Publish(
        AnalysisPolicyManifest manifest,
        CancellationToken cancellationToken = default);
}
