namespace AssetBlock.SearchEvaluation.VectorOperations;

public static class VectorMath
{
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector dimension mismatch: {a.Length} vs {b.Length}.");
        }

        var dot = 0.0;
        var normA = 0.0;
        var normB = 0.0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        if (normA <= 0.0 || normB <= 0.0)
        {
            return 0.0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    public static void ValidateVector(float[]? vector, int expectedDimension)
    {
        if (vector == null || vector.Length == 0)
        {
            throw new InvalidOperationException("Model returned empty or null vector.");
        }

        if (vector.Length != expectedDimension)
        {
            throw new InvalidOperationException($"Vector dimension mismatch: expected {expectedDimension}, got {vector.Length}.");
        }

        var sumSq = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            var val = vector[i];
            if (float.IsNaN(val) || float.IsInfinity(val))
            {
                throw new InvalidOperationException($"Vector contains non-finite value at index {i}: {val}.");
            }

            sumSq += (double)val * val;
        }

        if (sumSq < 1e-12)
        {
            throw new InvalidOperationException("Vector has zero Euclidean norm (all zeros).");
        }
    }
}
