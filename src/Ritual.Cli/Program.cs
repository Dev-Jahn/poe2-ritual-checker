using System.Text.Json;
using OpenCvSharp;
using Ritual.Core;

if (args.Length == 0)
{
    Console.WriteLine(
        "Ritual.Cli analyze <png-or-directory> --data <data> --out <directory> [--ocr]\nRitual.Cli window-state <directory> --data <data> --out <json>\nRitual.Cli leagues\nRitual.Cli price <catalog-id> <league> --data <data>"
    );
    return;
}
string Option(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args[0] == "economy-cache")
{
    using var ninja = new NinjaMarket(Option("--db", MarketStore.DefaultPath));
    var league = args[1];
    await ninja.RefreshAsync(league, default);
    var catalog = JsonFiles.Read<Catalog>(Path.Combine(Option("--data", "data"), "catalog.json"));
    var prices = catalog
        .Items.Select(i => new
        {
            i.Id,
            i.NameEn,
            Result = ninja.GetQuote(i, league),
        })
        .ToArray();
    Console.WriteLine(
        JsonSerializer.Serialize(
            new
            {
                summary = ninja.Summary(league),
                rate = ninja.GetRate(league),
                priced = prices.Count(x => x.Result.Quote is not null),
                items = prices,
            },
            JsonFiles.Options
        )
    );
    return;
}
if (args[0] == "capture")
{
    var game =
        System
            .Diagnostics.Process.GetProcesses()
            .FirstOrDefault(p =>
                p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase)
                && p.MainWindowHandle != 0
            ) ?? throw new InvalidOperationException("게임 창 없음");
    using var capture = new WgcCapture(game.MainWindowHandle);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var captured = await capture.CaptureAsync(default);
    var target = Path.GetFullPath(Option("--out", "work/live-capture.png"));
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    Cv2.ImWrite(target, captured.Image);
    JsonFiles.Write(
        target + ".json",
        new
        {
            source = "real",
            ui = "controller-ko",
            captured.Backend,
            captured.ColorStatus,
            capture.DisplayColorInfo,
            captured.ScreenBounds,
            captureMs = watch.Elapsed.TotalMilliseconds,
        }
    );
    Console.WriteLine(captured.ColorStatus);
    return;
}
if (args[0] == "leagues")
{
    using var scout = new ScoutMarket();
    Console.WriteLine(
        JsonSerializer.Serialize(await scout.LeaguesAsync(default), JsonFiles.Options)
    );
    return;
}
var data = Path.GetFullPath(Option("--data", "data"));
if (args[0] == "window-state")
{
    using var detector = new VisionEngine(data);
    GridObservation? previous = null;
    var results = new List<object>();
    foreach (var path in Directory.GetFiles(args[1], "*.png", SearchOption.AllDirectories).Order())
    {
        using var pixels = Cv2.ImRead(path);
        var detected = detector.DetectGrid(pixels);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var visible = previous is not null && detector.VerifyRitualWindow(pixels, previous);
        var elapsed = watch.Elapsed.TotalMilliseconds;
        results.Add(
            new
            {
                file = Path.GetFileName(path),
                detected,
                trackedVisible = previous is null ? detected is not null : visible,
                deferMode = detected is not null && detector.IsDeferMode(pixels, detected),
                checkMs = elapsed,
            }
        );
        previous = detected ?? previous;
    }
    JsonFiles.Write(Option("--out", "work/window-state.json"), results);
    Console.WriteLine($"Checked {results.Count} frames");
    return;
}
if (args[0] == "price")
{
    var catalog = JsonFiles.Read<Catalog>(Path.Combine(data, "catalog.json"));
    var item = catalog.Items.Single(i => i.Id == args[1]);
    using var scout = new ScoutMarket();
    var rate = await scout.RateAsync(args[2], default);
    if (item.Kind == "currency")
        Console.WriteLine(
            JsonSerializer.Serialize(
                new { quote = await scout.QuoteAsync(item, args[2], default), rate },
                JsonFiles.Options
            )
        );
    else
    {
        using var trade = new TradeMarket();
        var (q, listings) = await trade.QuoteAsync(item, args[2], rate, null, default);
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    quote = q,
                    rate,
                    listings,
                },
                JsonFiles.Options
            )
        );
    }
    return;
}
if (args[0] != "analyze")
    throw new ArgumentException("Unknown command");
using var vision = new VisionEngine(
    data,
    Option("--method", "template"),
    !args.Contains("--no-user-examples")
);
var ocr = new TextReaderEngine();
var output = Path.GetFullPath(Option("--out", "work/analysis"));
Directory.CreateDirectory(output);
ocr.SingleQuantityItems.UnionWith(
    vision.Catalog.Items.Where(RitualQuantity.IsSingle).Select(i => i.Id)
);
ocr.LoadDigitReference(Path.Combine(data, "ui", "quantity-one.png"));
var paths = Directory.Exists(args[1]) ? Directory.GetFiles(args[1], "*.png") : new[] { args[1] };
var summary = new List<object>();
foreach (var path in paths)
{
    using var frame = Cv2.ImRead(path);
    if (frame.Empty())
        throw new InvalidDataException(path);
    var result = vision.Analyze(frame, 1);
    if (args.Contains("--ocr") && ocr.Available)
        result = await ocr.ReadQuantitiesAsync(frame, result, default);
    if (args.Contains("--ocr") && ocr.Available && result.TooltipBounds is { } tb)
    {
        var tooltip = await ocr.ReadTooltipAsync(frame, result, vision.Catalog, default);
        using var tr = new Mat(frame, tb.Rect());
        var raw = await ocr.ReadAsync(tr, default);
        JsonFiles.Write(
            Path.Combine(output, Path.GetFileNameWithoutExtension(path) + ".tooltip.json"),
            new
            {
                tooltip,
                associatedInstance = tooltip is null
                    ? null
                    : TooltipBinding.Resolve(result, tooltip, vision.Catalog)?.InstanceId,
                lines = raw.Lines.Select(l => l.Text),
            }
        );
    }
    JsonFiles.Write(Path.Combine(output, Path.GetFileNameWithoutExtension(path) + ".json"), result);
    if (result.Grid is { } grid)
        Cv2.Rectangle(frame, grid.Bounds.Rect(), new Scalar(30, 220, 30), 2);
    for (int i = 0; i < result.Items.Length; i++)
    {
        var item = result.Items[i];
        Cv2.Rectangle(frame, item.Bounds.Rect(), new Scalar(10, 160, 255), 1);
        Cv2.PutText(
            frame,
            $"{i}:{item.CatalogId} {item.Confidence:0.00}",
            new Point(item.Bounds.X + 2, item.Bounds.Y + 15),
            HersheyFonts.HersheySimplex,
            .28,
            Scalar.White,
            1
        );
    }
    Cv2.ImWrite(Path.Combine(output, Path.GetFileName(path)), frame);
    summary.Add(
        new
        {
            file = Path.GetFileName(path),
            grid = result.Grid?.Bounds,
            items = result.Items.Length,
            elapsedMs = result.ElapsedMs,
        }
    );
    Console.WriteLine(
        $"{Path.GetFileName(path)}: {result.Items.Length} items / {result.ElapsedMs:0}ms / grid={result.Grid?.Bounds}"
    );
}
JsonFiles.Write(Path.Combine(output, "summary.json"), summary);
