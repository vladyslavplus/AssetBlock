namespace AssetBlock.Domain.Core;

public static class VectorValidation
{
    public static void Validate(float[]? vector, int expectedDimension)
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
