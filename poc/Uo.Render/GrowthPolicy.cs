namespace Uo.Render;

/// <summary>When a grow-on-demand GPU array (page arrays, the instance buffer) grows, and to what size. Pure logic, no
/// device: the owner asks <see cref="Check"/> once per frame, between frames (in the copy pass, before the frame's
/// uploads), and grows to <see cref="Next"/> when it says so.
///
/// Rules, in order:
/// <list type="number">
/// <item>At the ceiling, never.</item>
/// <item>Starved (the array is full and something waits for room: a loaded page with no free layer, an allocation that
/// failed): now. Growing beats evicting a page or dropping a chunk while the ceiling allows it.</item>
/// <item>Below <see cref="GrowNumerator"/>/<see cref="GrowDenominator"/> occupancy (75%): no, and the deferral resets.</item>
/// <item>At 75% or more during a hold or teleport (the host shows its previous frame, so a stall is not seen): now.</item>
/// <item>At 75% or more for <see cref="DeferFrames"/> frames without a hold: now, so the array still grows ahead of
/// need when no hold comes.</item>
/// </list></summary>
public struct GrowthPolicy
{
    public const int GrowNumerator = 3, GrowDenominator = 4;
    /// <summary>Frames at 75% or more before growing outside a hold (about 1 s at 120 fps).</summary>
    public const int DeferFrames = 120;

    long _highSince;
    bool _high;

    /// <summary>True when the array should grow this frame. <paramref name="used"/> and <paramref name="capacity"/> are
    /// in the array's own unit (layers, instances); <paramref name="max"/> is the ceiling.</summary>
    public bool Check(long used, long capacity, long max, bool hold, bool starved, long frame)
    {
        if (capacity >= max) { _high = false; return false; }
        if (starved) return true;
        if (used * GrowDenominator < capacity * GrowNumerator) { _high = false; return false; }
        if (hold) return true;
        if (!_high) { _high = true; _highSince = frame; }
        return frame - _highSince >= DeferFrames;
    }

    /// <summary><see cref="Check"/> for a batch that is about to place <paramref name="incoming"/> items (the loaded pages
    /// one upload pass will place, within its budget): starved when they don't all fit in the free room, so the array
    /// grows before the batch evicts or defers instead of after.</summary>
    public bool CheckBatch(long used, long capacity, long max, long incoming, bool hold, long frame)
        => Check(used, capacity, max, hold, Starved(used, capacity, incoming), frame);

    /// <summary>True when <paramref name="incoming"/> more items don't fit next to <paramref name="used"/>.</summary>
    public static bool Starved(long used, long capacity, long incoming) => incoming > 0 && used + incoming > capacity;

    /// <summary>The size to grow to so that <paramref name="needed"/> items fit: doubled until it does, at most the
    /// ceiling, and at least one doubling (<see cref="Next"/>).</summary>
    public static int NextFor(int capacity, int max, long needed)
    {
        int c = Next(capacity, max);
        while (c < max && c < needed) c = Next(c, max);
        return c;
    }

    /// <summary>Call after growing: the deferral restarts for the new size.</summary>
    public void Grew() => _high = false;

    /// <summary>The next size: double, at most the ceiling.</summary>
    public static int Next(int capacity, int max) => (int)Math.Min(max, Math.Max(1L, (long)capacity * 2));
}

/// <summary>Growth counters of one array: how often it grew and the main-thread time each step took (creating the larger
/// resource and recording the GPU copy; the copy itself runs on the GPU inside the frame).</summary>
public struct GrowthStats
{
    public int Count;
    public double LastMs, MaxMs, TotalMs;
    public int LastFrom, LastTo;

    public void Add(double ms, int from, int to)
    {
        Count++;
        LastMs = ms;
        MaxMs = Math.Max(MaxMs, ms);
        TotalMs += ms;
        LastFrom = from;
        LastTo = to;
    }
}
