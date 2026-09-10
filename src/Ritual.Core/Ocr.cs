using System.Text.RegularExpressions;
using OpenCvSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Ritual.Core;

public sealed class TextReaderEngine
{
    private readonly List<Mat> ones = [];
    public HashSet<string> NonStackable { get; } = [];

    public void LoadDigitReference(string path)
    {
        foreach (var old in ones)
            old.Dispose();
        ones.Clear();
        if (File.Exists(path))
            ones.Add(Cv2.ImRead(path, ImreadModes.Grayscale));
        var directory = Path.GetDirectoryName(path);
        if (Directory.Exists(directory))
            foreach (var variant in Directory.GetFiles(directory, "quantity-one-*.png"))
                ones.Add(Cv2.ImRead(variant, ImreadModes.Grayscale));
    }

    private readonly OcrEngine? engine = OcrEngine.TryCreateFromLanguage(new Language("ko-KR"));
    private readonly SemaphoreSlim gate = new(1);
    public bool Available => engine is not null;

    public async Task<OcrResult> ReadAsync(Mat image, CancellationToken token)
    {
        if (engine is null)
            throw new InvalidOperationException("Windows 한국어 OCR 언어팩이 필요합니다.");
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            using var resized = new Mat();
            double scale = Math.Min(
                1,
                (double)OcrEngine.MaxImageDimension / Math.Max(image.Width, image.Height)
            );
            Cv2.Resize(image, resized, new Size(), scale, scale);
            Cv2.ImEncode(".png", resized, out byte[] bytes);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied
            );
            var result = await engine.RecognizeAsync(bitmap);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Analysis> ReadQuantitiesAsync(
        Mat frame,
        Analysis analysis,
        CancellationToken token
    )
    {
        if (engine is null || analysis.Grid is null)
            return analysis;
        var grid = analysis.Grid;
        using var crop = new Mat(frame, grid.Bounds.Rect());
        var result = await ReadAsync(crop, token);
        double scale = Math.Min(
            1,
            (double)OcrEngine.MaxImageDimension / Math.Max(crop.Width, crop.Height)
        );
        var words = result.Lines.SelectMany(l => l.Words).ToArray();
        var items = analysis
            .Items.Select(item =>
            {
                if (
                    item.Kind != "currency"
                    || item.CatalogId is { } id && NonStackable.Contains(id)
                )
                    return item;
                var numbers = words
                    .Where(w =>
                    {
                        var x = w.BoundingRect.X / scale + grid.Bounds.X;
                        var y = w.BoundingRect.Y / scale + grid.Bounds.Y;
                        return x >= item.Bounds.X - 3
                            && x < item.Bounds.X + grid.CellSize * .20
                            && y >= item.Bounds.Y - 3
                            && y < item.Bounds.Y + grid.CellSize * .20
                            && w.BoundingRect.Height / scale < grid.CellSize * .40;
                    })
                    .Select(w => w.Text.Replace(",", ""))
                    .Where(t => Regex.IsMatch(t, @"^\d{1,4}$"))
                    .Select(int.Parse)
                    .Where(n => n > 0)
                    .Distinct()
                    .ToArray();
                return item with { Quantity = numbers.Length == 1 ? numbers[0] : null };
            })
            .ToArray();
        var unknown = items.Where(i => i.Kind == "currency" && i.Quantity is null).ToArray();
        if (unknown.Length > 0)
        {
            const int tileW = 160,
                tileH = 100;
            using var sheet = new Mat(
                ((unknown.Length + 3) / 4) * tileH,
                4 * tileW,
                MatType.CV_8UC3,
                Scalar.White
            );
            for (int i = 0; i < unknown.Length; i++)
            {
                var b = unknown[i].Bounds;
                var region = new Box(
                    b.X + 2,
                    b.Y + 2,
                    (int)(grid.CellSize * .50),
                    (int)(grid.CellSize * .42)
                );
                using var digit = new Mat(frame, region.Rect());
                using var hsv = new Mat();
                Cv2.CvtColor(digit, hsv, ColorConversionCodes.BGR2HSV);
                using var mask = new Mat();
                Cv2.InRange(hsv, new Scalar(0, 0, 170), new Scalar(180, 85, 255), mask);
                Cv2.BitwiseNot(mask, mask);
                using var rgb = new Mat();
                Cv2.CvtColor(mask, rgb, ColorConversionCodes.GRAY2BGR);
                using var enlarged = new Mat();
                Cv2.Resize(rgb, enlarged, new Size(100, 70), 0, 0, InterpolationFlags.Nearest);
                using var dest = new Mat(
                    sheet,
                    new Rect(i % 4 * tileW + 20, i / 4 * tileH + 15, 100, 70)
                );
                enlarged.CopyTo(dest);
            }
            var digitResult = await ReadAsync(sheet, token);
            var digitWords = digitResult.Lines.SelectMany(l => l.Words).ToArray();
            for (int i = 0; i < unknown.Length; i++)
            {
                var values = digitWords
                    .Where(w =>
                        w.BoundingRect.X >= i % 4 * tileW
                        && w.BoundingRect.X < (i % 4 + 1) * tileW
                        && w.BoundingRect.Y >= i / 4 * tileH
                        && w.BoundingRect.Y < (i / 4 + 1) * tileH
                    )
                    .Select(w => w.Text.Replace(",", ""))
                    .Where(t => Regex.IsMatch(t, @"^\d{1,4}$"))
                    .Select(int.Parse)
                    .Where(n => n > 0)
                    .Distinct()
                    .ToArray();
                if (values.Length == 1)
                {
                    var index = Array.FindIndex(items, x => x.InstanceId == unknown[i].InstanceId);
                    items[index] = items[index] with { Quantity = values[0] };
                }
            }
        }
        if (ones.Count > 0)
        {
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item.Kind != "currency" || NonStackable.Contains(item.CatalogId ?? ""))
                    continue;
                bool verified = false;
                foreach (int threshold in new[] { 170, 140, 100, 70 })
                {
                    if (verified)
                        break;
                    var b = item.Bounds;
                    using var digit = new Mat(
                        frame,
                        new Rect(
                            b.X + 3,
                            b.Y + 3,
                            (int)(grid.CellSize * .43),
                            (int)(grid.CellSize * .44)
                        )
                    );
                    using var hsv = new Mat();
                    Cv2.CvtColor(digit, hsv, ColorConversionCodes.BGR2HSV);
                    using var mask = new Mat();
                    Cv2.InRange(hsv, new Scalar(0, 0, threshold), new Scalar(180, 85, 255), mask);
                    Cv2.FindContours(
                        mask,
                        out Point[][] contours,
                        out _,
                        RetrievalModes.External,
                        ContourApproximationModes.ApproxSimple
                    );
                    var rects = contours
                        .Select(Cv2.BoundingRect)
                        .Where(r => r.Height > grid.CellSize * .18 && r.Width > 1)
                        .ToArray();
                    var leading = rects
                        .Where(r =>
                            r.X > grid.CellSize * .03
                            && r.X < grid.CellSize * .18
                            && r.Y < grid.CellSize * .16
                            && r.Width <= r.Height * .50
                            && r.Height < grid.CellSize * .4
                        )
                        .OrderBy(r => r.X)
                        .ToArray();
                    if (leading.Length != 1)
                        continue;
                    var first = leading[0];
                    if (
                        rects.Any(r =>
                            r != first
                            && Math.Abs(r.Y - first.Y) <= 3
                            && Math.Abs(r.Bottom - first.Bottom) <= 3
                        )
                    )
                        continue;
                    rects = [first];
                    using var area = new Mat(mask, rects[0]);
                    foreach (var one in ones)
                    {
                        using var normalized = new Mat();
                        Cv2.Resize(area, normalized, one.Size(), 0, 0, InterpolationFlags.Nearest);
                        using var scores = new Mat();
                        Cv2.MatchTemplate(normalized, one, scores, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(scores, out double _, out double score);
                        if (
                            rects[0].X < grid.CellSize * .18
                            && rects[0].Y < grid.CellSize * .16
                            && score > (one == ones[0] ? .80 : .85)
                        )
                        {
                            items[i] = item with { Quantity = 1 };
                            verified = true;
                        }
                    }
                }
            }
        }
        return analysis with { Items = items };
    }

    public async Task<TooltipInfo?> ReadTooltipAsync(
        Mat frame,
        Analysis analysis,
        Catalog catalog,
        CancellationToken token
    )
    {
        if (
            engine is null
            || analysis.TooltipBounds is not { } bounds
            || !VisionEngine.Within(bounds, frame)
        )
            return null;
        using var crop = new Mat(frame, bounds.Rect());
        var result = await ReadAsync(crop, token);
        double scale = Math.Min(
            1,
            (double)OcrEngine.MaxImageDimension / Math.Max(crop.Width, crop.Height)
        );
        var kept = new List<string>();
        var flavor = new List<string>();
        var blueLines = new List<(int Top, string Text)>();
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0)
                continue;
            int left = Math.Clamp(
                    (int)(line.Words.Min(w => w.BoundingRect.X) / scale),
                    0,
                    crop.Width - 1
                ),
                top = Math.Clamp(
                    (int)(line.Words.Min(w => w.BoundingRect.Y) / scale),
                    0,
                    crop.Height - 1
                );
            int right = Math.Clamp(
                    (int)(line.Words.Max(w => w.BoundingRect.Right) / scale),
                    left + 1,
                    crop.Width
                ),
                bottom = Math.Clamp(
                    (int)(line.Words.Max(w => w.BoundingRect.Bottom) / scale),
                    top + 1,
                    crop.Height
                );
            int orange = 0,
                blue = 0,
                ink = 0;
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var p = hsv.At<Vec3b>(y, x);
                if (p.Item2 < 120)
                    continue;
                ink++;
                if (p.Item0 is >= 110 and <= 135 && p.Item1 > 40)
                    blue++;
                if (p.Item0 is >= 5 and <= 24 && p.Item1 > 110)
                    orange++;
            }
            if (blue > 8 && blue > ink * .45)
                blueLines.Add((top, line.Text));
            if (top > analysis.Grid!.CellSize * 1.5 && orange > 8 && orange > ink * .45)
                flavor.Add(line.Text);
            else
                kept.Add(line.Text);
        }
        var cost = result.Lines.FirstOrDefault(l => TooltipParser.Key(l.Text) == "비용");
        if (cost is not null && analysis.Grid is { } grid)
        {
            int top = Math.Clamp(
                (int)(cost.Words.Max(w => w.BoundingRect.Bottom) / scale),
                0,
                crop.Height - 1
            );
            var box = new Rect(
                (int)(crop.Width * .25),
                top,
                (int)(crop.Width * .48),
                Math.Min(crop.Height - top, (int)(grid.CellSize * .75))
            );
            using var costHsv = new Mat(hsv, box);
            using var gold = new Mat();
            Cv2.InRange(costHsv, new Scalar(15, 35, 95), new Scalar(42, 200, 255), gold);
            Cv2.BitwiseNot(gold, gold);
            using var large = new Mat();
            Cv2.Resize(gold, large, new Size(), 3, 3, InterpolationFlags.Nearest);
            using var rgb = new Mat();
            Cv2.CvtColor(large, rgb, ColorConversionCodes.GRAY2BGR);
            var read = await ReadAsync(rgb, token);
            var costLines = read.Lines.Select(l => l.Text).ToArray();
            if (costLines.Any(l => TooltipParser.Key(l).Contains("공물점수")))
                kept.AddRange(costLines);
        }
        // A separate implicit block can repeat an explicit stat (e.g. Dexterity).
        // Use the visible horizontal separator, never simply take the last number.
        using var orangeMask = new Mat();
        Cv2.InRange(hsv, new Scalar(5, 70, 45), new Scalar(30, 255, 230), orangeMask);
        using var horizontal = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(Math.Max(20, (int)(analysis.Grid!.CellSize * 2)), 1)
        );
        Cv2.MorphologyEx(orangeMask, orangeMask, MorphTypes.Open, horizontal);
        Cv2.FindContours(
            orangeMask,
            out Point[][] dividers,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple
        );
        var separator = dividers
            .Select(Cv2.BoundingRect)
            .Where(r =>
                r.Height < 6
                && blueLines.Any(l => l.Top < r.Y)
                && blueLines.Any(l => l.Top > r.Bottom)
            )
            .Select(r => r.Bottom)
            .DefaultIfEmpty(0)
            .Max();
        var options = blueLines.Where(l => l.Top > separator).Select(l => l.Text).ToArray();
        var parsed = TooltipParser.Parse(
            kept.ToArray(),
            catalog,
            analysis.DeferMode,
            options.Length > 0 ? options : null
        );
        return parsed is null
            ? null
            : parsed with
            {
                RawText = string.Join('\n', result.Lines.Select(l => l.Text)),
                FlavorLines = flavor.ToArray(),
            };
    }
}

public static class TooltipParser
{
    private static readonly Regex Numbers = new(@"[+−-]?\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex Ranges = new(
        @"\([+−-]?\d+(?:\.\d+)?\s*[—–−-]\s*[+−-]?\d+(?:\.\d+)?\)",
        RegexOptions.Compiled
    );

    public static string Key(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}#]", "");

    public static string ModKey(string s) => Key(Numbers.Replace(Ranges.Replace(s, "#"), "#"));

    public static double[] Values(string s) =>
        Numbers
            .Matches(s)
            .Select(m =>
                double.Parse(
                    m.Value.Replace(",", "").Replace('−', '-'),
                    System.Globalization.CultureInfo.InvariantCulture
                )
            )
            .ToArray();

    public static TooltipInfo? Parse(
        string[] lines,
        Catalog catalog,
        bool deferMode = false,
        string[]? optionLines = null
    )
    {
        if (lines.Length == 0)
            return null;
        var raw = string.Join('\n', lines);
        var nameKeys = lines.Take(4).Select(Key).ToHashSet();
        var matches = catalog
            .Items.Where(i =>
                (i.NameKo.Length > 0 && nameKeys.Contains(Key(i.NameKo)))
                || nameKeys.Contains(Key(i.NameEn))
            )
            .ToArray();
        if (matches.Length == 0)
        {
            var near = catalog
                .Items.Where(i => i.NameKo.Length >= 4)
                .Select(i => new
                {
                    Item = i,
                    Distance = nameKeys.Select(k => Distance(k, Key(i.NameKo))).Min(),
                })
                .OrderBy(x => x.Distance)
                .Take(2)
                .ToArray();
            if (
                near.Length > 0
                && near[0].Distance <= Math.Max(1, Key(near[0].Item.NameKo).Length / 6)
                && (near.Length == 1 || near[1].Distance >= near[0].Distance + 2)
            )
                matches = [near[0].Item];
        }
        if (matches.Length != 1)
            return null;
        var item = matches[0];
        var mods = new Dictionary<string, double[]>();
        bool complete = item.ModsKo.Length > 0 && item.ModsKo.Length == item.ModsEn.Length;
        for (int i = 0; i < Math.Min(item.ModsKo.Length, item.ModsEn.Length); i++)
        {
            var key = ModKey(item.ModsKo[i]);
            var matching = (optionLines ?? lines).Where(l => ModKey(l) == key).ToArray();
            if (matching.Length == 0)
            {
                var near = (optionLines ?? lines)
                    .Select(l => (Line: l, Distance: Distance(ModKey(l), key)))
                    .OrderBy(l => l.Distance)
                    .Take(2)
                    .ToArray();
                if (
                    near.Length > 0
                    && near[0].Distance <= (key.Length >= 8 ? 2 : 1)
                    && (near.Length == 1 || near[1].Distance >= near[0].Distance + 2)
                )
                    matching = [near[0].Line];
            }
            if (
                matching.Length == 1
                && TranslateNumbers(item.ModsKo[i], item.ModsEn[i], matching[0]) is { } values
            )
                mods[ModKey(item.ModsEn[i])] = values;
            else
                complete = false;
        }
        int? purchase = null,
            defer = null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!Key(lines[i]).Contains("공물점수"))
                continue;
            var costLine = Regex.Replace(
                lines[i],
                @"[x×X]\s*([0-9lIO,]+)",
                m => "x" + m.Groups[1].Value.Replace('l', '1').Replace('I', '1').Replace('O', '0')
            );
            var nums = Values(costLine);
            if (nums.Length == 0 && i + 1 < lines.Length)
                nums = Values(lines[i + 1]);
            if (
                nums.Length != 1
                || nums[0] <= 0
                || nums[0] > int.MaxValue
                || nums[0] != Math.Truncate(nums[0])
            )
                continue;
            bool isDefer =
                deferMode
                || string.Join(' ', lines.Skip(Math.Max(0, i - 2)).Take(3)).Contains("보류");
            if (isDefer)
                defer = (int)nums[0];
            else
                purchase = (int)nums[0];
        }
        return new(
            item.Id,
            VisionEngine.DisplayName(item),
            lines,
            mods,
            purchase,
            defer,
            raw.Contains("타락"),
            complete,
            raw
        );
    }

    public static string[] FormatOptions(TooltipInfo info, CatalogItem item)
    {
        var result = new List<string>();
        for (int i = 0; i < Math.Min(item.ModsKo.Length, item.ModsEn.Length); i++)
        {
            if (!info.Mods.TryGetValue(ModKey(item.ModsEn[i]), out var values))
                continue;
            var tokens = Regex.Matches(item.ModsEn[i], Ranges + "|" + Numbers);
            if (tokens.Count != values.Length)
                continue;
            var variables = new Queue<double>(
                tokens
                    .Select((t, n) => (t, n))
                    .Where(x => Ranges.IsMatch(x.t.Value))
                    .Select(x => values[x.n])
            );
            if (Ranges.Matches(item.ModsKo[i]).Count != variables.Count)
                continue;
            result.Add(
                Ranges.Replace(
                    item.ModsKo[i],
                    _ =>
                        variables
                            .Dequeue()
                            .ToString("G", System.Globalization.CultureInfo.InvariantCulture)
                )
            );
        }
        return result.ToArray();
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1];
            next[0] = i;
            for (int j = 1; j <= b.Length; j++)
                next[j] = Math.Min(
                    Math.Min(next[j - 1] + 1, row[j] + 1),
                    row[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)
                );
            row = next;
        }
        return row[b.Length];
    }

    public static double[]? TranslateNumbers(string koTemplate, string enTemplate, string observed)
    {
        var tokenPattern = Ranges + "|" + Numbers;
        var koTokens = Regex.Matches(koTemplate, tokenPattern);
        var actual = Values(observed);
        if (koTokens.Count != actual.Length)
            return null;
        var variables = new Queue<double>();
        for (int i = 0; i < koTokens.Count; i++)
        {
            if (Ranges.IsMatch(koTokens[i].Value))
            {
                var range = OptionValuation.Intervals(koTokens[i].Value)[0];
                if (actual[i] < range.Min || actual[i] > range.Max)
                    return null;
                variables.Enqueue(actual[i]);
            }
            else if (Values(koTokens[i].Value)[0] != actual[i])
                return null;
        }
        var result = new List<double>();
        foreach (Match m in Regex.Matches(enTemplate, tokenPattern))
        {
            if (Ranges.IsMatch(m.Value))
            {
                if (!variables.TryDequeue(out var v))
                    return null;
                result.Add(v);
            }
            else
                result.Add(Values(m.Value)[0]);
        }
        return variables.Count == 0 ? result.ToArray() : null;
    }
}
