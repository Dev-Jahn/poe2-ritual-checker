namespace Ritual.Core;

public sealed record RegionCandidate(int X, int Y, int Width, int Height, double Score, int Index);

public static class RegionPartitioner
{
    private sealed record Trace(int Index, Trace? Previous);

    private sealed record State(UInt128 Covered, double Score, Trace? Trace);

    // Compare complete partitions: a good match for one cell must not split a
    // better-supported multi-cell item. The beam bounds worst-case CPU/memory.
    public static int[] Select(IEnumerable<RegionCandidate> candidates, int width, int height)
    {
        if (width <= 0 || height <= 0 || width * height > 128)
            throw new ArgumentOutOfRangeException(nameof(width));
        var groups = candidates
            .GroupBy(p => p.Y * width + p.X)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var states = new List<State> { new(0, 0, null) };
        for (int cell = 0; cell < width * height; cell++)
        {
            var next = new Dictionary<UInt128, State>();
            void Add(State state)
            {
                if (!next.TryGetValue(state.Covered, out var old) || state.Score > old.Score)
                    next[state.Covered] = state;
            }
            foreach (var state in states)
            {
                if (
                    (state.Covered & ((UInt128)1 << cell)) != 0
                    || !groups.TryGetValue(cell, out var options)
                )
                {
                    Add(state);
                    continue;
                }
                foreach (var p in options)
                {
                    UInt128 mask = 0;
                    for (int y = p.Y; y < p.Y + p.Height; y++)
                    for (int x = p.X; x < p.X + p.Width; x++)
                        mask |= (UInt128)1 << (y * width + x);
                    if ((mask & state.Covered) != 0)
                        continue;
                    double evidence = Math.Max(.01, p.Score);
                    var covered = state.Covered | mask;
                    double score = state.Score + p.Width * p.Height * evidence * evidence;
                    if (!next.TryGetValue(covered, out var existing) || score > existing.Score)
                        next[covered] = new(covered, score, new(p.Index, state.Trace));
                }
            }
            // Past coverage cannot affect future choices; merge equivalent frontiers.
            UInt128 past = cell == 127 ? UInt128.MaxValue : ((UInt128)1 << (cell + 1)) - 1;
            states = next
                .Values.GroupBy(s => s.Covered & ~past)
                .Select(g => g.MaxBy(s => s.Score)!)
                .OrderByDescending(s => s.Score)
                .Take(96)
                .ToList();
        }
        var regions = new List<int>();
        for (
            var trace = states.MaxBy(s => s.Score)?.Trace;
            trace is not null;
            trace = trace.Previous
        )
            regions.Add(trace.Index);
        regions.Reverse();
        return regions.ToArray();
    }
}
