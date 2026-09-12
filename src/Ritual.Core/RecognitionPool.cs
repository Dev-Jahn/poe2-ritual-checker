namespace Ritual.Core;

public sealed record RitualPoolRule(
    string[] Leagues,
    string[] ExcludedIds,
    string Source,
    string Reason
);

public sealed record RitualPoolManifest(int SchemaVersion, RitualPoolRule[] Rules);

public sealed class RecognitionPool
{
    private readonly RitualPoolRule[] rules;

    public RecognitionPool(string path)
    {
        var manifest = File.Exists(path) ? JsonFiles.Read<RitualPoolManifest>(path) : new(1, []);
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException("의식 출현 목록 버전 오류");
        rules = manifest.Rules;
    }

    public HashSet<string> Excluded(string? league) =>
        rules
            .Where(r => r.Leagues.Contains(league, StringComparer.OrdinalIgnoreCase))
            .SelectMany(r => r.ExcludedIds)
            .ToHashSet(StringComparer.Ordinal);
}
