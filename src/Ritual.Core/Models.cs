using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ritual.Core;

public record Box(int X, int Y, int Width, int Height)
{
    [JsonIgnore]
    public int Right => X + Width;

    [JsonIgnore]
    public int Bottom => Y + Height;

    public bool Intersects(Box b) => X < b.Right && Right > b.X && Y < b.Bottom && Bottom > b.Y;

    public OpenCvSharp.Rect Rect() => new(X, Y, Width, Height);
}

public record CatalogItem(
    string Id,
    string NameEn,
    string NameKo,
    string Kind,
    string Image,
    int Width,
    int Height,
    string Source,
    string ImageSource,
    string[] ModsEn,
    string[] ModsKo,
    int? MaxStackSize = null
);

public record Catalog(string Version, DateTimeOffset RetrievedAt, CatalogItem[] Items);

public record Candidate(string Id, string Name, double Score);

public record ItemObservation(
    string InstanceId,
    Box Bounds,
    int Column,
    int Row,
    int Columns,
    int Rows,
    string? CatalogId,
    string Name,
    string Kind,
    int? Quantity,
    double Confidence,
    bool Estimated,
    bool Deferred,
    bool Dimmed,
    bool Selected,
    Candidate[] Candidates
);

public record GridObservation(Box Bounds, double CellSize, double Confidence);

public record Analysis(
    long Generation,
    string Fingerprint,
    GridObservation? Grid,
    ItemObservation[] Items,
    Box? TooltipBounds,
    string[] Warnings,
    double ElapsedMs,
    bool DeferMode = false
);

public record TooltipInfo(
    string? CatalogId,
    string Name,
    string[] Lines,
    Dictionary<string, double[]> Mods,
    int? PurchaseTribute,
    int? DeferTribute,
    bool Corrupted,
    bool Complete,
    string RawText,
    string[]? FlavorLines = null
);

public record PriceQuote(
    string ItemId,
    string League,
    decimal UnitPrice,
    string Currency,
    DateTimeOffset RetrievedAt,
    string Source,
    string Basis,
    int Samples,
    bool Stale = false,
    string? Note = null
);

public record Rate(decimal ExaltedPerDivine, DateTimeOffset RetrievedAt);

public record FormattedPrice(string Text, decimal? TotalExalted, bool Estimated);

public record Settings
{
    public string League { get; set; } = "";
    public int KeyboardVirtualKey { get; set; } = 0x77; // F8
    public uint KeyboardModifiers { get; set; } = 0;
    public ushort ControllerButtons { get; set; } = 0x0300; // both shoulders
    public int ControllerHoldMs { get; set; } = 600;
    public int ControllerIndex { get; set; } = -1;
    public bool ControllerEnabled { get; set; } = true;
    public int WatchIntervalMs { get; set; } = 300;
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException(path);

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}

public sealed class GenerationGate
{
    private long current;
    public long Current => Interlocked.Read(ref current);

    public long Next() => Interlocked.Increment(ref current);

    public bool Accept(long value) => value == Current;
}
