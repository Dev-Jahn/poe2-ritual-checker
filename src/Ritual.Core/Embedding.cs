namespace Ritual.Core;

public record EmbeddingModel(
    string CatalogVersion,
    string Method,
    float[] Mean,
    float[][] Components,
    string? OfficialArtHash = null
)
{
    public float[] Project(float[] pixels)
    {
        var normalized = Normalize(pixels);
        for (int i = 0; i < normalized.Length; i++)
            normalized[i] -= Mean[i];
        var result = new float[Components.Length];
        for (int k = 0; k < Components.Length; k++)
        for (int i = 0; i < normalized.Length; i++)
            result[k] += normalized[i] * Components[k][i];
        var length = MathF.Sqrt(result.Sum(v => v * v));
        if (length > 0)
            for (int i = 0; i < result.Length; i++)
                result[i] /= length;
        return result;
    }

    public static float[] Normalize(float[] pixels)
    {
        float mean = pixels.Average();
        var length = MathF.Sqrt(pixels.Sum(p => (p - mean) * (p - mean)));
        return pixels.Select(p => (p - mean) / Math.Max(.001f, length)).ToArray();
    }

    public static double Similarity(float[] a, float[] b)
    {
        double result = 0;
        for (int i = 0; i < a.Length; i++)
            result += a[i] * b[i];
        return result;
    }
}
