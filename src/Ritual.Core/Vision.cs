using System.Diagnostics;
using System.Security.Cryptography;
using OpenCvSharp;

namespace Ritual.Core;

public sealed class VisionEngine : IDisposable
{
    private readonly Catalog catalog;
    private readonly Mat empty;
    private readonly Mat title;
    private readonly Mat tribute;
    private readonly Mat deferModeIcon;
    private readonly Mat deferActionLabel;
    private readonly Mat deferredMarker;
    private readonly List<Reference> references = [];
    private readonly EmbeddingModel? embedding;
    private readonly string method;

    private sealed record Reference(
        CatalogItem Item,
        float[] Gray,
        float[] Edge,
        float[] Mask,
        float[] Color,
        float[]? Embedding,
        bool Manual = false
    );

    private sealed record Proposal(
        int X,
        int Y,
        int W,
        int H,
        Candidate[] Candidates,
        double Score
    );

    private sealed record OfficialArt(string CatalogId, string Image);

    private sealed record OfficialManifest(OfficialArt[] Items);

    public Catalog Catalog => catalog;

    public VisionEngine(
        string dataDirectory,
        string method = "template",
        bool useUserExamples = true
    )
    {
        Cv2.SetNumThreads(Math.Min(4, Environment.ProcessorCount));
        catalog = JsonFiles.Read<Catalog>(Path.Combine(dataDirectory, "catalog.json"));
        if (method is not ("template" or "embedding" or "hybrid"))
            throw new ArgumentException("Unknown recognition method");
        this.method = method;
        if (method != "template")
        {
            embedding = JsonFiles.Read<EmbeddingModel>(
                Path.Combine(dataDirectory, "embedding.json")
            );
            if (
                embedding.OfficialArtHash
                    != (
                        File.Exists(Path.Combine(dataDirectory, "official-references.json"))
                            ? Convert.ToHexString(
                                SHA256.HashData(
                                    File.ReadAllBytes(
                                        Path.Combine(dataDirectory, "official-references.json")
                                    )
                                )
                            )
                            : null
                    )
                || embedding.CatalogVersion != catalog.Version
                || embedding.Mean.Length != 1152
                || embedding.Components.Any(c => c.Length != 1152)
            )
                throw new InvalidDataException("임베딩 모델과 카탈로그 버전 불일치");
        }
        deferredMarker = Cv2.ImRead(
            Path.Combine(dataDirectory, "ui", "deferred-marker.png"),
            ImreadModes.Grayscale
        );
        empty = Cv2.ImRead(
            Path.Combine(dataDirectory, "ui", "empty-cell.png"),
            ImreadModes.Grayscale
        );
        title = Cv2.ImRead(
            Path.Combine(dataDirectory, "ui", "ritual-title.png"),
            ImreadModes.Grayscale
        );
        tribute = Cv2.ImRead(
            Path.Combine(dataDirectory, "ui", "tribute-icon.png"),
            ImreadModes.Grayscale
        );
        deferModeIcon = Cv2.ImRead(
            Path.Combine(dataDirectory, "ui", "defer-mode-icon.png"),
            ImreadModes.Grayscale
        );
        deferActionLabel = Cv2.ImRead(
            Path.Combine(dataDirectory, "ui", "defer-action-label.png"),
            ImreadModes.Grayscale
        );
        if (empty.Empty())
            throw new InvalidDataException(
                "격자 참조 이미지가 없습니다. tools/prepare_ui.py를 실행하세요."
            );
        var officialPath = Path.Combine(dataDirectory, "official-references.json");
        var official = File.Exists(officialPath)
            ? JsonFiles
                .Read<OfficialManifest>(officialPath)
                .Items.ToDictionary(i => i.CatalogId, i => i.Image)
            : new Dictionary<string, string>();
        foreach (var item in catalog.Items)
        {
            using var rgba = Cv2.ImRead(
                Path.Combine(dataDirectory, official.GetValueOrDefault(item.Id, item.Image)),
                ImreadModes.Unchanged
            );
            if (rgba.Empty() || rgba.Channels() != 4 || item.Width > 2 || item.Height > 4)
                continue;
            foreach (
                var (scale, shift, shiftY) in item.Id.StartsWith("Omen_")
                && official.ContainsKey(item.Id)
                    ? new[]
                    {
                        (1.0, 0.0, 0.0),
                        (.96, -.025, 0.0),
                        (.96, -.025, -.025),
                        (.78, 0.0, 0.0),
                    }
                : item.Kind == "currency" && item.Width == 1 && item.Height == 1
                    ? new[] { (1.0, 0.0, 0.0), (.78, 0.0, 0.0) }
                : new[] { (1.0, 0.0, 0.0) }
            )
            {
                using var variant = new Mat(rgba.Size(), rgba.Type(), Scalar.All(0));
                using var affine = Mat.FromArray(
                    new double[,]
                    {
                        { scale, 0, rgba.Width * ((1 - scale) / 2 + shift) },
                        { 0, scale, rgba.Height * ((1 - scale) / 2 + shiftY) },
                    }
                );
                Cv2.WarpAffine(
                    rgba,
                    variant,
                    affine,
                    rgba.Size(),
                    InterpolationFlags.Linear,
                    BorderTypes.Constant,
                    Scalar.All(0)
                );
                using var bgr = new Mat();
                Cv2.CvtColor(variant, bgr, ColorConversionCodes.BGRA2BGR);
                using var alpha = new Mat();
                Cv2.ExtractChannel(variant, alpha, 3);
                for (int py = 0; py < bgr.Height; py++)
                for (int px = 0; px < bgr.Width; px++)
                {
                    var pixel = bgr.At<Vec3b>(py, px);
                    float opacity = alpha.At<byte>(py, px) / 255f;
                    bgr.Set(
                        py,
                        px,
                        new Vec3b(
                            (byte)(pixel.Item0 * opacity),
                            (byte)(pixel.Item1 * opacity),
                            (byte)(pixel.Item2 * opacity)
                        )
                    );
                }
                var (gray, edge) = Describe(bgr);
                using var resized = new Mat();
                Cv2.Resize(alpha, resized, new Size(24, 48));
                var mask = new float[24 * 48];
                for (int y = 0; y < 48; y++)
                for (int x = 0; x < 24; x++)
                    mask[y * 24 + x] =
                        resized.At<byte>(y, x) > 80
                        && x > 1
                        && x < 22
                        && y > 1
                        && y < 46
                        && !(
                            item.Width == 1
                            && item.Height == 1
                            && ((x < 8 && y < 15) || (x > 15 && y > 31))
                        )
                            ? resized.At<byte>(y, x) / 255f
                            : 0;
                references.Add(
                    new(item, gray, edge, mask, ColorPixels(bgr), embedding?.Project(gray))
                );
                if (
                    item.Kind == "unique"
                    && item.Width == 2
                    && item.Height == 2
                    && item.ImageSource.Contains("/Foci/")
                )
                {
                    using var padded = new Mat(
                        new Size(bgr.Width, bgr.Height * 3 / 2),
                        bgr.Type(),
                        Scalar.All(0)
                    );
                    using (
                        var target = new Mat(
                            padded,
                            new Rect(0, bgr.Height / 4, bgr.Width, bgr.Height)
                        )
                    )
                        bgr.CopyTo(target);
                    using var paddedAlpha = new Mat(padded.Size(), MatType.CV_8UC1, Scalar.All(0));
                    using (
                        var target = new Mat(
                            paddedAlpha,
                            new Rect(0, bgr.Height / 4, bgr.Width, bgr.Height)
                        )
                    )
                        alpha.CopyTo(target);
                    using var smallAlpha = new Mat();
                    Cv2.Resize(paddedAlpha, smallAlpha, new Size(24, 48));
                    var pm = new float[1152];
                    for (int yy = 2; yy < 46; yy++)
                    for (int xx = 2; xx < 22; xx++)
                        pm[yy * 24 + xx] =
                            smallAlpha.At<byte>(yy, xx) > 80
                                ? smallAlpha.At<byte>(yy, xx) / 255f
                                : 0;
                    var (pg, pe) = Describe(padded);
                    references.Add(
                        new(
                            item with
                            {
                                Height = 3,
                            },
                            pg,
                            pe,
                            pm,
                            ColorPixels(padded),
                            embedding?.Project(pg)
                        )
                    );
                }
                if (
                    item.Kind == "unique"
                    && item.Width == 1
                    && item.Height >= 3
                    && item.ImageSource.Contains("/Weapons/")
                )
                {
                    // Controller inventory reserves two cells for some one-cell weapon
                    // art. Preserve the art aspect ratio inside the wider slot.
                    using var padded = new Mat(
                        new Size(bgr.Width * 2, bgr.Height),
                        bgr.Type(),
                        Scalar.All(0)
                    );
                    using (
                        var target = new Mat(
                            padded,
                            new Rect(bgr.Width / 2, 0, bgr.Width, bgr.Height)
                        )
                    )
                        bgr.CopyTo(target);
                    using var paddedAlpha = new Mat(padded.Size(), MatType.CV_8UC1, Scalar.All(0));
                    using (
                        var target = new Mat(
                            paddedAlpha,
                            new Rect(bgr.Width / 2, 0, bgr.Width, bgr.Height)
                        )
                    )
                        alpha.CopyTo(target);
                    using var smallAlpha = new Mat();
                    Cv2.Resize(paddedAlpha, smallAlpha, new Size(24, 48));
                    var pm = new float[1152];
                    for (int yy = 2; yy < 46; yy++)
                    for (int xx = 2; xx < 22; xx++)
                        pm[yy * 24 + xx] =
                            smallAlpha.At<byte>(yy, xx) > 80
                                ? smallAlpha.At<byte>(yy, xx) / 255f
                                : 0;
                    var (pg, pe) = Describe(padded);
                    references.Add(
                        new(
                            item with
                            {
                                Width = 2,
                            },
                            pg,
                            pe,
                            pm,
                            ColorPixels(padded),
                            embedding?.Project(pg)
                        )
                    );
                }
            }
        }
        var examples = Path.Combine(dataDirectory, "user-examples");
        if (useUserExamples && Directory.Exists(examples))
            foreach (var path in Directory.GetFiles(examples, "*.json"))
            {
                try
                {
                    var example = JsonFiles.Read<UserExample>(path);
                    if (example.Source != "manual")
                        continue;
                    var known = catalog.Items.SingleOrDefault(i => i.Id == example.CatalogId);
                    if (known is null)
                        continue;
                    using var pixels = Cv2.ImRead(Path.ChangeExtension(path, ".png"));
                    if (!pixels.Empty())
                        AddExampleReference(pixels, known);
                }
                catch (Exception ex)
                    when (ex is IOException or System.Text.Json.JsonException or OpenCVException)
                { }
            }
        if (references.Count == 0)
            throw new InvalidDataException("사용 가능한 아이템 참조 이미지가 없습니다.");
    }

    private sealed record UserExample(
        string CatalogId,
        string Source,
        string Session,
        string CatalogVersion
    );

    public void AddExample(Mat crop, string catalogId, string directory, string session)
    {
        var item = catalog.Items.Single(i => i.Id == catalogId);
        Directory.CreateDirectory(directory);
        Cv2.ImEncode(".png", crop, out var bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = Path.Combine(directory, hash);
        File.WriteAllBytes(path + ".png", bytes);
        JsonFiles.Write(
            path + ".json",
            new UserExample(item.Id, "manual", session, catalog.Version)
        );
        AddExampleReference(crop, item);
    }

    private void AddExampleReference(Mat crop, CatalogItem item)
    {
        var (g, e) = Describe(crop);
        var mask = new float[1152];
        for (int y = 3; y < 45; y++)
        for (int x = 2; x < 22; x++)
            if (!(item.Width == 1 && item.Height == 1 && ((x < 8 && y < 15) || (x > 15 && y > 31))))
                mask[y * 24 + x] = 1;
        lock (references)
            references.Add(new(item, g, e, mask, ColorPixels(crop), embedding?.Project(g), true));
    }

    public Analysis Analyze(Mat bgr, long generation, GridObservation? knownGrid = null)
    {
        var watch = Stopwatch.StartNew();
        var grid = knownGrid ?? DetectGrid(bgr);
        if (grid is null)
            return new(
                generation,
                "",
                null,
                [],
                null,
                ["의식 격자를 찾지 못했습니다."],
                watch.Elapsed.TotalMilliseconds
            );
        if (DetectConfirmation(bgr, grid) is not null)
            return new(
                generation,
                Fingerprint(bgr, grid),
                grid,
                [],
                null,
                ["확인 창이 열려 있어 분석을 잠시 중지합니다."],
                watch.Elapsed.TotalMilliseconds
            );
        var tooltip = DetectTooltip(bgr, grid);
        var occupied = new bool[10, 12];
        var observed = new bool[10, 12];
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var resizedEmpty = new Mat();
        var c = grid.CellSize;
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
        {
            var box = CellBox(grid, x, y, 1, 1);
            if (!Within(box, bgr) || tooltip?.Intersects(box) == true)
                continue;
            observed[y, x] = true;
            var inner = new Rect(
                box.X + (int)(c * .09),
                box.Y + (int)(c * .08),
                Math.Max(4, (int)(c * .80)),
                Math.Max(4, (int)(c * .80))
            );
            using var crop = new Mat(bgr, inner);
            int blue = 0,
                bright = 0,
                red = 0;
            for (int cy = 0; cy < crop.Rows; cy += 2)
            for (int cx = 0; cx < crop.Cols; cx += 2)
            {
                var p = crop.At<Vec3b>(cy, cx);
                if (p.Item0 > p.Item2 * 1.3 + 4 && p.Item0 > p.Item1 * 1.15 + 3)
                    blue++;
                if (p.Item2 > p.Item0 * 1.3 + 4 && p.Item2 > p.Item1 * 1.3 + 4)
                    red++;
                if (Math.Max(p.Item0, Math.Max(p.Item1, p.Item2)) > 65)
                    bright++;
            }
            double n = Math.Ceiling(crop.Rows / 2.0) * Math.Ceiling(crop.Cols / 2.0);
            occupied[y, x] = blue / n > .14 || red / n > .14 || bright / n > .08;
        }
        var proposals = new List<Proposal>();
        Reference[] snapshot;
        lock (references)
            snapshot = references.ToArray();
        var groups = snapshot.GroupBy(r => (r.Item.Width, r.Item.Height)).ToArray();
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
        {
            if (!occupied[y, x])
                continue;
            foreach (var group in groups)
            {
                var (w, h) = group.Key;
                if (x + w > 12 || y + h > 10)
                    continue;
                bool valid = true;
                for (int dy = 0; dy < h; dy++)
                for (int dx = 0; dx < w; dx++)
                    if (!occupied[y + dy, x + dx])
                        valid = false;
                if (!valid)
                    continue;
                var box = CellBox(grid, x, y, w, h);
                using var roi = new Mat(bgr, box.Rect());
                var (g, e) = Describe(roi);
                var color = ColorPixels(roi);
                var embedded = embedding?.Project(g);
                var candidates = group
                    .Select(r => new Candidate(
                        r.Item.Id,
                        DisplayName(r.Item),
                        Score(r, g, e, color, embedded)
                    ))
                    .GroupBy(r => r.Id)
                    .Select(g => g.MaxBy(r => r.Score)!)
                    .OrderByDescending(r => r.Score)
                    .Take(3)
                    .ToArray();
                if (candidates.Length > 0)
                    proposals.Add(new(x, y, w, h, candidates, candidates[0].Score));
            }
        }
        var used = new bool[10, 12];
        var items = new List<ItemObservation>();
        foreach (var p in proposals.OrderByDescending(p => p.Score))
        {
            bool overlaps = false;
            for (int dy = 0; dy < p.H; dy++)
            for (int dx = 0; dx < p.W; dx++)
                if (used[p.Y + dy, p.X + dx])
                    overlaps = true;
            if (overlaps)
                continue;
            for (int dy = 0; dy < p.H; dy++)
            for (int dx = 0; dx < p.W; dx++)
                used[p.Y + dy, p.X + dx] = true;
            var item = catalog.Items.First(i => i.Id == p.Candidates[0].Id);
            var box = CellBox(grid, p.X, p.Y, p.W, p.H);
            using var crop = new Mat(bgr, box.Rect());
            using var hsv = new Mat();
            Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
            var mean = Cv2.Mean(hsv);
            bool selected = Selected(bgr, box, c);
            // Score is similarity, not a calibrated probability.
            var margin = p.Candidates.Length > 1 ? p.Score - p.Candidates[1].Score : 1;
            items.Add(
                new(
                    $"{p.X}:{p.Y}",
                    box,
                    p.X,
                    p.Y,
                    p.W,
                    p.H,
                    item.Id,
                    DisplayName(item),
                    item.Kind,
                    item.Kind == "unique" || item.MaxStackSize == 1 ? 1 : ReadQuantity(crop),
                    p.Score,
                    p.Score < .80 || margin < .08,
                    Deferred(crop, c),
                    IsDimmed(crop),
                    selected,
                    p.Candidates
                )
            );
        }
        var missing = 0;
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
            if (occupied[y, x] && !used[y, x])
                missing++;
        var warnings = new List<string> { "실험적 인식 엔진 · 독립 정확도 검증 미완료" };
        if (tooltip is not null)
            warnings.Add("툴팁으로 가려진 셀은 미관측 처리");
        if (missing > 0)
            warnings.Add($"분류하지 못한 점유 셀 {missing}개");
        bool deferMode = IsDeferMode(bgr, grid);
        if (deferMode)
            warnings.Add("보류 모드 · 비용은 구매 공물과 구분");
        return new(
            generation,
            Fingerprint(bgr, grid),
            grid,
            items.OrderBy(i => i.Row).ThenBy(i => i.Column).ToArray(),
            tooltip,
            warnings.ToArray(),
            watch.Elapsed.TotalMilliseconds,
            deferMode
        );
    }

    private static Box? DetectConfirmation(Mat frame, GridObservation grid)
    {
        var region = new Rect(
            grid.Bounds.X,
            grid.Bounds.Y,
            Math.Min(frame.Width - grid.Bounds.X, (int)(grid.CellSize * 22)),
            grid.Bounds.Height
        );
        using var crop = new Mat(frame, region);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 25, 80);
        Cv2.FindContours(
            edges,
            out Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple
        );
        foreach (var contour in contours)
        {
            var r = Cv2.BoundingRect(contour);
            if (
                r.Width < grid.CellSize * 7
                || r.Height < grid.CellSize * 2
                || r.Height > grid.CellSize * 5
                || r.Width < r.Height * 2.3
                || Math.Abs(Cv2.ContourArea(contour)) < r.Width * r.Height * .95
            )
                continue;
            using var panel = new Mat(gray, r);
            using var dark = new Mat();
            Cv2.InRange(panel, new Scalar(0), new Scalar(60), dark);
            if (Cv2.CountNonZero(dark) < r.Width * r.Height * .70)
                continue;
            return new(r.X + region.X, r.Y + region.Y, r.Width, r.Height);
        }
        return null;
    }

    private double Score(
        Reference reference,
        float[] gray,
        float[] edge,
        float[] color,
        float[]? vector
    )
    {
        var template =
            .70 * Correlation(gray, reference.Gray, reference.Mask)
            + .30 * Correlation(edge, reference.Edge, reference.Mask);
        var learned = vector is not null
            ? EmbeddingModel.Similarity(vector, reference.Embedding!)
            : 0;
        var score =
            (
                method == "template" ? template
                : method == "embedding" ? learned
                : .65 * template + .35 * learned
            ) - ColorPenalty(color, reference.Color, reference.Mask);
        return reference.Manual && score < .70 ? -1 : score;
    }

    private static IEnumerable<(Point p, double s)[]> ConnectedCells(
        (Point p, double s)[] peaks,
        int cell
    )
    {
        var remaining = peaks.ToList();
        while (remaining.Count > 0)
        {
            var group = new List<(Point p, double s)> { remaining[0] };
            remaining.RemoveAt(0);
            for (int index = 0; index < group.Count; index++)
            for (int i = remaining.Count - 1; i >= 0; i--)
                if (
                    Math.Abs(remaining[i].p.X - group[index].p.X) < cell * 1.5
                    && Math.Abs(remaining[i].p.Y - group[index].p.Y) < cell * 1.5
                )
                {
                    group.Add(remaining[i]);
                    remaining.RemoveAt(i);
                }
            yield return group.ToArray();
        }
    }

    public GridObservation? DetectGrid(Mat bgr)
    {
        if (bgr.Width > bgr.Height * 2.5)
        {
            int width = Math.Min(bgr.Width, (int)(bgr.Height * 1.6)),
                step = width / 2;
            var starts = new[] { (bgr.Width - width) / 2 }
                .Concat(
                    Enumerable
                        .Range(0, (bgr.Width - width + step - 1) / step + 1)
                        .Select(n => Math.Min(n * step, bgr.Width - width))
                )
                .Distinct();
            foreach (int left in starts)
            {
                using var tile = new Mat(bgr, new Rect(left, 0, width, bgr.Height));
                var detected = DetectGrid(tile);
                if (detected is not null)
                    return detected with
                    {
                        Bounds = detected.Bounds with { X = detected.Bounds.X + left },
                    };
            }
            return null;
        }
        double factor = Math.Min(1, 1500.0 / Math.Max(bgr.Width, bgr.Height));
        using var small = new Mat();
        Cv2.Resize(bgr, small, new Size(), factor, factor);
        using var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        double bestQuality = 0;
        GridObservation? best = null;
        int bestCell = 60;
        void TryScale(int cell)
        {
            using var template = new Mat();
            Cv2.Resize(
                empty,
                template,
                new Size((int)Math.Round(cell * 60.0 / 70), (int)Math.Round(cell * 63.0 / 70))
            );
            if (template.Width >= gray.Width || template.Height >= gray.Height)
                return;
            using var scores = new Mat();
            Cv2.MatchTemplate(gray, template, scores, TemplateMatchModes.CCoeffNormed);
            var peaks = new List<(Point p, double s)>();
            for (int n = 0; n < 120; n++)
            {
                Cv2.MinMaxLoc(scores, out _, out double score, out _, out Point point);
                if (score < .76)
                    break;
                peaks.Add((point, score));
                Cv2.Rectangle(
                    scores,
                    new Rect(
                        Math.Max(0, point.X - cell / 3),
                        Math.Max(0, point.Y - cell / 3),
                        Math.Min(scores.Width - Math.Max(0, point.X - cell / 3), cell * 2 / 3 + 1),
                        Math.Min(scores.Height - Math.Max(0, point.Y - cell / 3), cell * 2 / 3 + 1)
                    ),
                    Scalar.All(-1),
                    -1
                );
            }
            if (peaks.Count < 8)
                return;
            // Inventory can have the sharpest cell. Check each lattice phase before choosing a grid.
            var remaining = peaks.ToList();
            while (remaining.Count >= 8)
            {
                var anchor = remaining[0].p;
                var aligned = remaining
                    .Where(p =>
                        Math.Abs(
                            (p.p.X - anchor.X) / (double)cell
                                - Math.Round((p.p.X - anchor.X) / (double)cell)
                        ) < .12
                        && Math.Abs(
                            (p.p.Y - anchor.Y) / (double)cell
                                - Math.Round((p.p.Y - anchor.Y) / (double)cell)
                        ) < .12
                    )
                    .ToArray();
                remaining.RemoveAll(p => aligned.Contains(p));
                if (aligned.Length < 8)
                    continue;
                // A tooltip can split one grid's visible cells. Prefer the combined evidence;
                // disconnected components also handle separate windows with the same lattice phase.
                foreach (
                    var component in new[] { aligned }.Concat(
                        ConnectedCells(aligned, cell).Where(g => g.Length < aligned.Length)
                    )
                )
                {
                    if (component.Length < 8)
                        continue;
                    int right = component.Max(p => p.p.X),
                        top = component.Min(p => p.p.Y);
                    var xs = component
                        .Select(p =>
                            (i: 11 - Math.Round((right - p.p.X) / (double)cell), v: (double)p.p.X)
                        )
                        .ToArray();
                    var ys = component
                        .Select(p =>
                            (i: Math.Round((p.p.Y - top) / (double)cell), v: (double)p.p.Y)
                        )
                        .ToArray();
                    double Slope((double i, double v)[] a)
                    {
                        var mi = a.Average(z => z.i);
                        var mv = a.Average(z => z.v);
                        var denominator = a.Sum(z => (z.i - mi) * (z.i - mi));
                        return denominator > 0
                            ? a.Sum(z => (z.i - mi) * (z.v - mv)) / denominator
                            : cell;
                    }
                    var spacing = (Slope(xs) + Slope(ys)) / 2;
                    var x0 = xs.Average(z => z.v - z.i * spacing) - cell * 6.0 / 70;
                    var y0 = ys.Average(z => z.v - z.i * spacing) - cell * 4.0 / 70;
                    var box = new Box(
                        (int)Math.Round(x0 / factor),
                        (int)Math.Round(y0 / factor),
                        (int)Math.Round(spacing * 12 / factor),
                        (int)Math.Round(spacing * 10 / factor)
                    );
                    if (!Within(box, bgr))
                        continue;
                    double quality = component.Length * component.Average(p => p.s);
                    var candidate = new GridObservation(
                        box,
                        spacing / factor,
                        component.Average(p => p.s)
                    );
                    if (quality > bestQuality && VerifyRitualWindow(bgr, candidate))
                    {
                        bestQuality = quality;
                        bestCell = cell;
                        best = candidate;
                    }
                }
            }
        }
        int max = Math.Min(116, Math.Min(gray.Width / 12, gray.Height / 10));
        TryScale((int)Math.Round(70 * factor));
        if (bestQuality >= 40 && best is not null)
            return best;
        if (bestQuality < 20)
            for (int cell = 16; cell <= max; cell += 2)
                TryScale(cell);
        int coarse = bestCell;
        for (int cell = Math.Max(16, coarse - 3); cell <= Math.Min(max, coarse + 3); cell++)
            TryScale(cell);
        return best;
    }

    private static double MatchUi(
        Mat bgr,
        GridObservation grid,
        Mat template,
        double x,
        double y,
        double width,
        double height
    )
    {
        var c = grid.CellSize;
        var box = new Box(
            (int)(grid.Bounds.X + c * x),
            (int)(grid.Bounds.Y + c * y),
            (int)(c * width),
            (int)(c * height)
        );
        if (template.Empty() || !Within(box, bgr))
            return 0;
        using var crop = new Mat(bgr, box.Rect());
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var reference = new Mat();
        Cv2.Resize(template, reference, new Size(), c / 70, c / 70);
        if (reference.Width > gray.Width || reference.Height > gray.Height)
            return 0;
        using var scores = new Mat();
        Cv2.MatchTemplate(gray, reference, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out double max);
        return max;
    }

    public bool VerifyRitualWindow(Mat bgr, GridObservation grid)
    {
        if (MatchUi(bgr, grid, title, 4, -2.8, 4, 1.5) > .60)
            return true;
        // Defer help covers the title; the centered tribute line can shift as costs change.
        if (MatchUi(bgr, grid, tribute, 1.5, -1.55, 8.7, 1.5) > .73)
            return true;
        // Help and failure banners can cover every header anchor; the bottom action stays visible.
        return (
                MatchUi(bgr, grid, deferModeIcon, 10, -1.5, 1.3, 1.4) > .78
                || HasDeferAction(bgr, grid)
            ) && VerifyGridCells(bgr, grid);
    }

    private bool VerifyGridCells(Mat bgr, GridObservation grid)
    {
        if (!Within(grid.Bounds, bgr))
            return false;
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var reference = new Mat();
        Cv2.Resize(empty, reference, new Size(), grid.CellSize / 70, grid.CellSize / 70);
        int matches = 0;
        var rows = new HashSet<int>();
        foreach (int y in new[] { 0, 3, 6, 9 })
        foreach (int x in new[] { 2, 5, 8, 11 })
        {
            using var cell = new Mat(gray, CellBox(grid, x, y, 1, 1).Rect());
            if (cell.Width < reference.Width || cell.Height < reference.Height)
                continue;
            using var scores = new Mat();
            Cv2.MatchTemplate(cell, reference, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out double max);
            if (max > .76)
            {
                matches++;
                rows.Add(y);
                if (matches >= 6 && rows.Count >= 2)
                    return true;
            }
        }
        return false;
    }

    private bool HasDeferAction(Mat bgr, GridObservation grid) =>
        MatchUi(bgr, grid, deferActionLabel, 4, 11.3, 4, 1) > .82;

    public bool IsDeferMode(Mat bgr, GridObservation grid)
    {
        var c = grid.CellSize;
        var b = new Box(
            (int)(grid.Bounds.X + 10 * c),
            (int)(grid.Bounds.Y - 1.5 * c),
            (int)(1.25 * c),
            (int)(1.3 * c)
        );
        if (!Within(b, bgr))
            return HasDeferAction(bgr, grid);
        using var crop = new Mat(bgr, b.Rect());
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        using var red = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 130, 70), new Scalar(8, 255, 255), red);
        return Cv2.CountNonZero(red) / (double)(red.Width * red.Height) > .12
            || HasDeferAction(bgr, grid);
    }

    public static Box CellBox(GridObservation grid, int x, int y, int w, int h)
    {
        int left = (int)Math.Round(grid.Bounds.X + x * grid.CellSize),
            top = (int)Math.Round(grid.Bounds.Y + y * grid.CellSize);
        return new(
            left,
            top,
            (int)Math.Round(grid.Bounds.X + (x + w) * grid.CellSize) - left,
            (int)Math.Round(grid.Bounds.Y + (y + h) * grid.CellSize) - top
        );
    }

    public static bool Within(Box box, Mat image) =>
        box.X >= 0
        && box.Y >= 0
        && box.Width > 0
        && box.Height > 0
        && box.Right <= image.Width
        && box.Bottom <= image.Height;

    public static string DisplayName(CatalogItem item) =>
        string.IsNullOrWhiteSpace(item.NameKo) ? item.NameEn : item.NameKo;

    private static (float[] gray, float[] edge) Describe(Mat bgr)
    {
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(24, 48), 0, 0, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1);
        var g = new float[1152];
        var e = new float[1152];
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 24; x++)
        {
            g[y * 24 + x] = gray.At<byte>(y, x);
            e[y * 24 + x] = MathF.Abs(gx.At<float>(y, x)) + MathF.Abs(gy.At<float>(y, x));
        }
        return (g, e);
    }

    private static double Correlation(float[] a, float[] b, float[] mask)
    {
        var vn = System.Numerics.Vector<float>.Zero;
        var vsa = vn;
        var vsb = vn;
        var vaa = vn;
        var vbb = vn;
        var vab = vn;
        int count = System.Numerics.Vector<float>.Count,
            i = 0;
        for (; i <= a.Length - count; i += count)
        {
            var av = new System.Numerics.Vector<float>(a, i);
            var bv = new System.Numerics.Vector<float>(b, i);
            var m = new System.Numerics.Vector<float>(mask, i);
            var am = av * m;
            var bm = bv * m;
            vn += m;
            vsa += am;
            vsb += bm;
            vaa += av * am;
            vbb += bv * bm;
            vab += am * bv;
        }
        double n = System.Numerics.Vector.Sum(vn),
            sa = System.Numerics.Vector.Sum(vsa),
            sb = System.Numerics.Vector.Sum(vsb),
            aa = System.Numerics.Vector.Sum(vaa),
            bb = System.Numerics.Vector.Sum(vbb),
            ab = System.Numerics.Vector.Sum(vab);
        for (; i < a.Length; i++)
        {
            double m = mask[i];
            n += m;
            sa += a[i] * m;
            sb += b[i] * m;
            aa += a[i] * a[i] * m;
            bb += b[i] * b[i] * m;
            ab += a[i] * b[i] * m;
        }
        if (n < 8)
            return -1;
        return (ab - sa * sb / n) / Math.Sqrt(Math.Max(1, (aa - sa * sa / n) * (bb - sb * sb / n)));
    }

    private static float[] ColorPixels(Mat bgr)
    {
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(24, 48), 0, 0, InterpolationFlags.Area);
        var values = new float[1152 * 3];
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 24; x++)
        {
            var p = resized.At<Vec3b>(y, x);
            int i = (y * 24 + x) * 3;
            float sum = p.Item0 + p.Item1 + p.Item2;
            if (sum < 100)
                continue;
            values[i] = p.Item0 / sum;
            values[i + 1] = p.Item1 / sum;
            values[i + 2] = p.Item2 / sum;
        }
        return values;
    }

    private static double ColorPenalty(float[] a, float[] b, float[] mask)
    {
        float distance = 0,
            n = 0,
            saturation = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0)
                continue;
            int j = i * 3;
            if (a[j] + a[j + 1] + a[j + 2] < .5f || b[j] + b[j + 1] + b[j + 2] < .5f)
                continue;
            saturation +=
                MathF.Max(a[j], MathF.Max(a[j + 1], a[j + 2]))
                - MathF.Min(a[j], MathF.Min(a[j + 1], a[j + 2]));
            distance +=
                MathF.Abs(a[j] - b[j])
                + MathF.Abs(a[j + 1] - b[j + 1])
                + MathF.Abs(a[j + 2] - b[j + 2]);
            n++;
        }
        // Grayscale items keep their shape score; colored variants get a hue tie-break.
        return n > 12 && saturation / n > .10 ? .45 * distance / n : 0;
    }

    public static string Fingerprint(Mat bgr, GridObservation grid)
    {
        using var crop = new Mat(bgr, grid.Bounds.Rect());
        using var small = new Mat();
        Cv2.Resize(crop, small, new Size(120, 100));
        using var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        var data = new byte[12000];
        for (int y = 0; y < 100; y++)
        for (int x = 0; x < 120; x++)
            data[y * 120 + x] = (byte)(gray.At<byte>(y, x) / 16);
        return Convert.ToHexString(SHA256.HashData(data))[..24];
    }

    public static double SceneDifference(Mat before, Mat after, GridObservation grid)
    {
        if (before.Size() != after.Size() || !Within(grid.Bounds, after))
            return 1;
        using var a = new Mat(before, grid.Bounds.Rect());
        using var b = new Mat(after, grid.Bounds.Rect());
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        return Cv2.Mean(diff).Val0 / 255;
    }

    public static bool Selected(Mat image, Box box, double cell)
    {
        if (MouseHovered(image, box, cell))
            return true;
        // The selected slot has an upward chevron. A deferred item's flat gold
        // border is not selection and must not attach another item's tooltip.
        int cx = box.X + box.Width / 2;
        double unit = cell / 70;
        bool Bright(int dx, int dy)
        {
            int x = Math.Clamp(cx + (int)Math.Round(dx * unit), 0, image.Width - 1),
                y = Math.Clamp(box.Bottom + (int)Math.Round(dy * unit), 0, image.Height - 1);
            var p = image.At<Vec3b>(y, x);
            return p.Item2 > 150 && p.Item1 > 130 && p.Item0 > 45;
        }
        return Bright(0, -8)
            && Bright(-4, -5)
            && Bright(4, -5)
            && !Bright(-9, -8)
            && !Bright(9, -8);
    }

    internal static bool MouseHovered(Mat image, Box box, double cell)
    {
        if (!Within(box, image))
            return false;
        bool Green(int x, int y)
        {
            var p = image.At<Vec3b>(y, x);
            return p.Item1 >= 20 && p.Item1 > p.Item0 * 1.3 && p.Item1 > p.Item2 * 1.3;
        }
        int margin = Math.Max(3, (int)(cell * .08));
        bool Edge(bool horizontal)
        {
            int length = (horizontal ? box.Width : box.Height) - 2 * margin;
            if (length < 8)
                return false;
            for (int offset = 0; offset <= 1; offset++)
            {
                int green = 0;
                for (int i = margin; i < margin + length; i++)
                    if (Green(box.X + (horizontal ? i : offset), box.Y + (horizontal ? offset : i)))
                        green++;
                if (green > length * .7)
                    return true;
            }
            return false;
        }
        return Edge(true) && Edge(false);
    }

    private bool Deferred(Mat crop, double cell)
    {
        if (deferredMarker.Empty())
            return false;
        int w = Math.Min(crop.Width, (int)(cell * .50)),
            h = Math.Min(crop.Height, (int)(cell * .55));
        using var corner = new Mat(crop, new Rect(crop.Width - w, crop.Height - h, w, h));
        using var gray = new Mat();
        Cv2.CvtColor(corner, gray, ColorConversionCodes.BGR2GRAY);
        using var template = new Mat();
        Cv2.Resize(
            deferredMarker,
            template,
            new Size(
                Math.Max(4, (int)Math.Round(25 * cell / 70)),
                Math.Max(4, (int)Math.Round(28 * cell / 70))
            )
        );
        if (template.Width > gray.Width || template.Height > gray.Height)
            return false;
        using var scores = new Mat();
        Cv2.MatchTemplate(gray, template, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out double score);
        return score > .68;
    }

    private static bool IsDimmed(Mat crop)
    {
        // Measure foreground, excluding quantity, corner decorations and the blue
        // slot background. Darkness of the whole rectangle is not item state.
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        int bright = 0,
            neutral = 0;
        for (int y = 4; y < crop.Height - 4; y++)
        for (int x = 4; x < crop.Width - 4; x++)
        {
            if (
                (x < crop.Width * .32 && y < Math.Min(crop.Height * .4, 28))
                || (x > crop.Width - 30 && y > crop.Height - 35)
            )
                continue;
            var p = hsv.At<Vec3b>(y, x);
            if (p.Item2 < 40 || p.Item0 is >= 115 and <= 125 && p.Item1 > 160)
                continue;
            bright++;
            if (p.Item1 < 45)
                neutral++;
        }
        return bright > 15 && neutral > bright * .78;
    }

    private static int? ReadQuantity(Mat crop)
    {
        // Digits are read by the dedicated OCR pass, never assume a missing digit is one.
        return null;
    }

    public static Box? DetectTooltip(Mat bgr, GridObservation grid)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(4, 100, 100), new Scalar(25, 255, 255), mask);
        var horizontal = Cv2.HoughLinesP(
                mask,
                1,
                Math.PI / 180,
                60,
                grid.CellSize * 4,
                grid.CellSize * .3
            )
            .Where(l =>
                Math.Abs(l.P1.Y - l.P2.Y) < 4
                && Math.Min(l.P1.Y, l.P2.Y) >= grid.Bounds.Y - 8
                && Math.Min(l.P1.Y, l.P2.Y) < grid.Bounds.Bottom
                && Math.Min(l.P1.X, l.P2.X) > grid.Bounds.X + grid.CellSize * 2
            )
            .OrderBy(l => Math.Min(l.P1.Y, l.P2.Y))
            .ToArray();
        if (horizontal.Length > 0)
        {
            var line = horizontal[0];
            int x = Math.Max(0, Math.Min(line.P1.X, line.P2.X) - 30),
                y = Math.Max(0, Math.Min(line.P1.Y, line.P2.Y) - 4);
            return new(
                x,
                y,
                Math.Min(bgr.Width - x, Math.Abs(line.P1.X - line.P2.X) + 60),
                TooltipHeight(
                    bgr,
                    x,
                    y,
                    Math.Min(bgr.Width - x, Math.Abs(line.P1.X - line.P2.X) + 60),
                    grid.CellSize
                )
            );
        }
        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(Math.Max(9, (int)grid.CellSize / 3), 3)
        );
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        Cv2.FindContours(
            mask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple
        );
        var heads = contours
            .Select(Cv2.BoundingRect)
            .Where(r =>
                r.Width > grid.CellSize * 4
                && r.Height < grid.CellSize * 1.8
                && r.Y > grid.Bounds.Y - grid.CellSize
                && r.Y < grid.Bounds.Bottom
            )
            .OrderByDescending(r => r.Width)
            .ToArray();
        if (heads.Length == 0)
            return DetectMouseTooltipAboveItem(bgr, grid);
        var head = heads[0];
        int left = Math.Max(0, head.X - 8),
            top = Math.Max(0, head.Y - 8);
        return new(
            left,
            top,
            Math.Min(bgr.Width - left, head.Width + 16),
            TooltipHeight(
                bgr,
                left,
                top,
                Math.Min(bgr.Width - left, head.Width + 16),
                grid.CellSize
            )
        );
    }

    private static Box? DetectMouseTooltipAboveItem(Mat image, GridObservation grid)
    {
        var hovered = new List<Box>();
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 12; x++)
        {
            var box = CellBox(grid, x, y, 1, 1);
            if (MouseHovered(image, box, grid.CellSize))
                hovered.Add(box);
        }
        if (hovered.Count != 1 || hovered[0].Y < grid.CellSize)
            return null;
        var item = hovered[0];
        int left = Math.Max(0, item.X - (int)(grid.CellSize * 12));
        int right = Math.Min(image.Width, item.Right + (int)(grid.CellSize * 12));
        using var region = new Mat(image, new Rect(left, 0, right - left, item.Y));
        using var gray = new Mat();
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 35, 90);
        Cv2.FindContours(
            edges,
            out Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple
        );
        var headers = contours
            .Where(contour =>
            {
                var r = Cv2.BoundingRect(contour);
                return r.Width >= grid.CellSize * 4
                    && r.Height >= grid.CellSize * .25
                    && r.Height <= grid.CellSize * 1.8
                    && r.Width > r.Height * 4
                    && Math.Abs(Cv2.ContourArea(contour)) > r.Width * r.Height * .8
                    && r.X + left < item.Right
                    && r.Right + left > item.X
                    && item.Y - r.Bottom >= grid.CellSize;
            })
            .Select(Cv2.BoundingRect)
            .OrderByDescending(r => r.Width);
        foreach (var header in headers)
        {
            using var area = new Mat(gray, header);
            using var dark = new Mat();
            Cv2.InRange(area, Scalar.All(0), Scalar.All(55), dark);
            if (Cv2.CountNonZero(dark) < header.Width * header.Height * .75)
                continue;
            int x = Math.Max(0, left + header.X - (int)(grid.CellSize * .4));
            int y = Math.Max(0, header.Y - 4);
            int end = Math.Min(image.Width, left + header.Right + (int)(grid.CellSize * .15));
            return new(x, y, end - x, item.Y - y);
        }
        return null;
    }

    private static int TooltipHeight(Mat image, int x, int y, int width, double cell)
    {
        int height = Math.Min(image.Height - y, (int)(cell * 12));
        using var crop = new Mat(image, new Rect(x, y, width, height));
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        using var blue = new Mat();
        Cv2.InRange(hsv, new Scalar(95, 90, 110), new Scalar(118, 255, 255), blue);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(blue, blue, MorphTypes.Close, kernel);
        Cv2.FindContours(
            blue,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple
        );
        var buttons = contours
            .Select(Cv2.BoundingRect)
            .Where(r =>
                r.Y > cell * 3
                && r.X > width * .2
                && r.Right < width * .7
                && r.Width > cell * .18
                && r.Width < cell * .65
                && r.Height > cell * .18
                && r.Height < cell * .65
                && r.Width / (double)r.Height is > .7 and < 1.4
            )
            .OrderBy(r => r.Y)
            .ToArray();
        return buttons.Length == 0
            ? height
            : Math.Min(height, buttons[0].Bottom + (int)(cell * .25));
    }

    public void Dispose()
    {
        empty.Dispose();
        title.Dispose();
        tribute.Dispose();
        deferModeIcon.Dispose();
        deferActionLabel.Dispose();
        deferredMarker.Dispose();
    }
}
