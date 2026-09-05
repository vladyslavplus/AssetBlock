namespace AssetBlock.SearchEvaluation.Ollama;

public sealed record ModelVerificationResult(
    bool IsAvailable,
    string? ErrorMessage,
    string? ActualDigest);

public interface IOllamaEmbeddingClient
{
    Task<ModelVerificationResult> CheckModelAvailability(CancellationToken cancellationToken);
    Task<float[]> GenerateEmbedding(string text, CancellationToken cancellationToken);
}
