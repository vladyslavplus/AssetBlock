namespace AssetBlock.Domain.Abstractions.Services;

public sealed record GeneratedEmbedding(
    float[] Vector,
    string ModelKey,
    int Dimension);

public sealed record ModelVerificationResult(
    bool IsAvailable,
    string? FailureReason = null,
    string? ActualDigest = null);

public interface ITextEmbeddingGenerator
{
    Task<GeneratedEmbedding> Generate(string text, CancellationToken cancellationToken = default);
    Task<ModelVerificationResult> CheckModelAvailability(CancellationToken cancellationToken = default);
}
