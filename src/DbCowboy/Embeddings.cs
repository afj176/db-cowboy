using System.Globalization;
using System.Text.Json;
namespace DbCowboy;

public sealed record Projection(double[][] Points, double Explained, int Dimensions);
public static class Embeddings
{
    public static double[] Parse(string value) { var v = JsonSerializer.Deserialize<double[]>(value) ?? throw new FormatException("Expected a numeric JSON array."); if (v.Length == 0 || v.Length > 16000 || v.Any(x => !double.IsFinite(x))) throw new FormatException("Use 1–16,000 finite vector components."); return v; }
    public static string Serialize(double[] value) => "[" + string.Join(",", value.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
    // Deterministic, centered PCA using covariance-free power iteration. O(n*d), no d*d allocation.
    public static Projection Pca(IReadOnlyList<double[]> vectors, CancellationToken cancellationToken = default)
    {
        if (vectors.Count == 0) return new([], 0, 0); int n = vectors.Count, d = vectors[0].Length;
        if (vectors.Any(v => v.Length != d)) throw new ArgumentException("Vectors have different dimensions. Choose vectors with a single dimension before projecting.");
        var mean = new double[d]; foreach (var v in vectors) { cancellationToken.ThrowIfCancellationRequested(); for (int j = 0; j < d; j++) mean[j] += v[j] / n; }
        var centered = vectors.Select(v => v.Select((x, j) => x - mean[j]).ToArray()).ToArray();
        double Dot(double[] a, double[] b) { double s = 0; for (int j = 0; j < d; j++) s += a[j] * b[j]; return s; }
        double[] Component(double[]? prior, int seed) { var rand = new Random(seed); var a = Enumerable.Range(0, d).Select(_ => rand.NextDouble() - .5).ToArray(); for (int iter = 0; iter < 80; iter++) { cancellationToken.ThrowIfCancellationRequested(); var b = new double[d]; foreach (var v in centered) { var k = Dot(v, a); for (int j = 0; j < d; j++) b[j] += v[j] * k; } if (prior != null) { var k = Dot(b, prior); for (int j = 0; j < d; j++) b[j] -= k * prior[j]; } var norm = Math.Sqrt(Dot(b, b)); if (norm < 1e-12) return new double[d]; for (int j = 0; j < d; j++) a[j] = b[j] / norm; } return a; }
        var x = Component(null, 73); var y = Component(x, 91); var points = centered.Select(v => new[] { Dot(v, x), Dot(v, y) }).ToArray(); var total = centered.Sum(v => Dot(v, v)); var explained = total > 0 ? points.Sum(p => p[0] * p[0] + p[1] * p[1]) / total : 0; return new(points, Math.Clamp(explained, 0, 1), d);
    }
    public static double Norm(double[] vector) => Math.Sqrt(vector.Sum(x => x * x));
    public static double Cosine(double[] left, double[] right)
    {
        if (left.Length != right.Length) throw new ArgumentException("Embedding dimensions differ.");
        double norm = Norm(left) * Norm(right); if (norm == 0) return double.NaN;
        return Math.Clamp(left.Zip(right).Sum(p => p.First * p.Second) / norm, -1, 1);
    }
}
