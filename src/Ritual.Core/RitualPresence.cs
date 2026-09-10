namespace Ritual.Core;

public sealed class RitualPresence
{
    private int misses;

    // A single transition frame is insufficient evidence to stop the capture session.
    public bool Observe(bool visible)
    {
        misses = visible ? 0 : misses + 1;
        return misses >= 2;
    }

    public void Reset() => misses = 0;
}
