using OpenCvSharp;

namespace Ritual.Core;

public sealed record RecognitionConflict(
    string InstanceId,
    string Reason,
    string? PreviousId,
    string? ProposedId,
    double AppearanceSimilarity,
    bool Retained
);

public sealed record ContinuityResult(Analysis Analysis, RecognitionConflict[] Conflicts);

public static class RecognitionContinuity
{
    public static ContinuityResult Reconcile(
        Mat before,
        Analysis previous,
        Mat after,
        Analysis next
    )
    {
        if (
            previous.Grid is null
            || next.Grid is null
            || previous.Grid.Bounds != next.Grid.Bounds
            || previous.Items.Length == 0
            || next.Items.Length == 0
        )
            return new(next, []);
        var old = previous.Items.ToDictionary(i => i.InstanceId);
        var similarities = new Dictionary<string, double>();
        foreach (var item in next.Items)
            if (
                old.TryGetValue(item.InstanceId, out var prior)
                && prior.Bounds == item.Bounds
                && previous.TooltipBounds?.Intersects(prior.Bounds) != true
                && next.TooltipBounds?.Intersects(item.Bounds) != true
            )
                similarities[item.InstanceId] = AppearanceSimilarity(
                    before,
                    after,
                    item.Bounds,
                    item.Columns == 1 && item.Rows == 1
                );
        int anchors = next.Items.Count(i =>
            old.TryGetValue(i.InstanceId, out var p)
            && p.CatalogId == i.CatalogId
            && similarities.GetValueOrDefault(i.InstanceId) >= .90
        );
        // A matching position alone is insufficient after a reroll or a different Ritual.
        bool sameScene =
            anchors >= Math.Min(3, Math.Min(previous.Items.Length, next.Items.Length))
            && anchors >= Math.Min(previous.Items.Length, next.Items.Length) * .6;
        var conflicts = new List<RecognitionConflict>();
        var items = next
            .Items.Select(item =>
            {
                if (!old.TryGetValue(item.InstanceId, out var prior))
                    return item;
                bool geometryChanged = item.Bounds != prior.Bounds;
                if (!geometryChanged && item.CatalogId == prior.CatalogId)
                    return item;
                double similarity = similarities.GetValueOrDefault(item.InstanceId);
                var priorCandidate = item.Candidates.FirstOrDefault(c => c.Id == prior.CatalogId);
                bool retain =
                    sameScene
                    && !geometryChanged
                    && similarity >= .90
                    && priorCandidate is not null
                    && item.Confidence - priorCandidate.Score < .035
                    && (!prior.Dimmed || item.Dimmed);
                if (sameScene)
                    conflicts.Add(
                        new(
                            item.InstanceId,
                            geometryChanged ? "geometry-change" : "identity-change",
                            prior.CatalogId,
                            item.CatalogId,
                            similarity,
                            retain
                        )
                    );
                return retain
                    ? item with
                    {
                        CatalogId = prior.CatalogId,
                        Name = prior.Name,
                        Kind = prior.Kind,
                        Quantity =
                            prior.Kind == "unique" || prior.CatalogId?.StartsWith("Omen_") == true
                                ? 1
                                : item.Quantity,
                        Confidence = prior.Confidence,
                        Estimated = prior.Estimated,
                    }
                    : item;
            })
            .ToArray();
        return new(next with { Items = items }, conflicts.ToArray());
    }

    public static double AppearanceSimilarity(Mat before, Mat after, Box bounds, bool oneCell)
    {
        if (!VisionEngine.Within(bounds, before) || !VisionEngine.Within(bounds, after))
            return 0;
        var a = Features(before, bounds, oneCell);
        var b = Features(after, bounds, oneCell);
        double dot = 0,
            aa = 0,
            bb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }
        return dot / Math.Sqrt(Math.Max(1e-12, aa * bb));
    }

    private static float[] Features(Mat frame, Box bounds, bool oneCell)
    {
        using var roi = new Mat(frame, bounds.Rect());
        using var small = new Mat();
        Cv2.Resize(roi, small, new Size(24, 48), 0, 0, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        var values = new List<float>();
        for (int y = 4; y < 40; y++)
        for (int x = 3; x < 21; x++)
            // The defer badge's antialiased fringe also changes when the UI dims.
            if (!oneCell || !((x < 8 && y < 15) || (x >= 15 && y >= 31)))
                values.Add(gray.At<byte>(y, x));
        float mean = values.Average();
        return values.Select(v => v - mean).ToArray();
    }
}
