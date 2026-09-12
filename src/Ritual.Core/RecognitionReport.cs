using System.Security.Cryptography;
using OpenCvSharp;

namespace Ritual.Core;

public sealed record ReportImage(string File, string Sha256);

public sealed record RecognitionReportContext(
    string Reason,
    string Session,
    string League,
    string AppVersion,
    string CatalogVersion,
    string? ModelHash,
    string? SelectedInstance,
    string? CorrectedCatalogId,
    Analysis Prediction,
    Analysis? RawPrediction,
    Analysis? PreviousPrediction,
    RecognitionConflict[] Conflicts,
    Dictionary<string, PriceQuote> Prices,
    Dictionary<string, string> PriceStates,
    Dictionary<string, TooltipInfo> Tooltips
);

public static class RecognitionReport
{
    // The caller supplies game capture buffers. Never capture the desktop or overlay here.
    public static async Task<string> SaveAsync(
        string directory,
        Mat frame,
        RecognitionReportContext context,
        Mat? previous = null,
        Mat? observed = null
    )
    {
        using var copy = frame.Clone();
        using var before = previous?.Clone();
        using var latest = observed?.Clone();
        return await Task.Run(() =>
        {
            string id =
                DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff")
                + "-"
                + Guid.NewGuid().ToString("N")[..8];
            string folder = Path.Combine(directory, id);
            Directory.CreateDirectory(folder);
            ReportImage Write(string name, Mat image)
            {
                Cv2.ImEncode(".png", image, out var bytes);
                File.WriteAllBytes(Path.Combine(folder, name), bytes);
                return new(name, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            var original = Write("capture.png", copy);
            var prior = before is null ? null : Write("previous.png", before);
            var current = latest is null ? null : Write("observed.png", latest);
            JsonFiles.Write(
                Path.Combine(folder, "capture.json"),
                new
                {
                    schemaVersion = 1,
                    source = "real",
                    independent = false,
                    session = context.Session,
                    split = "unassigned",
                    fullFrame = true,
                    resolution = new[] { copy.Width, copy.Height },
                    sha256 = original.Sha256,
                    prediction = context.Prediction,
                    groundTruth = (object?)null,
                    report = context,
                    images = new
                    {
                        original,
                        previous = prior,
                        observed = current,
                    },
                    note = "A report or a candidate conflict is not a verified label. observed.png may be newer than the analysis frame.",
                }
            );
            return folder;
        });
    }
}
