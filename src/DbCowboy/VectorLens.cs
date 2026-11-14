namespace DbCowboy;

public sealed record VectorNeighbor(int Index, double Similarity);
public static class VectorLens
{
    public static List<VectorNeighbor> Neighbors(IReadOnlyList<double[]> vectors, double[] seed, int limit = 12) => vectors
        .Select((v, i) => new VectorNeighbor(i, Embeddings.Cosine(seed, v)))
        .Where(n => double.IsFinite(n.Similarity)).OrderByDescending(n => n.Similarity).ThenBy(n => n.Index).Take(limit).ToList();

    // Signed mean of contiguous component bands; a fingerprint, not a semantic
    // interpretation of dimensions. Preserve signs and normalize only at render.
    public static double[] Fingerprint(double[] vector, int bands = 64)
    {
        if (bands < 1) throw new ArgumentOutOfRangeException(nameof(bands));
        int count = Math.Min(bands, vector.Length); var values = new double[count];
        for (int i = 0; i < count; i++) { int start = i * vector.Length / count, end = (i + 1) * vector.Length / count; values[i] = vector[start..end].Average(); }
        return values;
    }
}
