using System.Runtime.CompilerServices;

namespace Uo.Render;

/// <summary>Exact dynamic page membership; timestamps are refreshed every frame, never retained across eviction.</summary>
public sealed class DynamicPageTouchCache
{
    readonly uint[] _sequence, _unique;
    readonly ulong[] _seen = new ulong[2048]; // art and indexed namespaces, each with 65536 possible page ids
    int _count = -1, _uniqueCount;

    public DynamicPageTouchCache(int capacity)
    {
        _sequence = new uint[capacity];
        _unique = new uint[Math.Min(capacity, 131072)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Key(in SpriteInstance sprite) => (uint)sprite.Page
        | ((sprite.Flags & SpriteInstance.FlagIndexed) != 0 ? 0x10000u : 0);

    public ReadOnlySpan<uint> Update(ReadOnlySpan<SpriteInstance> sprites)
    {
        bool same = sprites.Length == _count;
        for (int i = 0; same && i < sprites.Length; i++) same = _sequence[i] == Key(in sprites[i]);
        if (same) return _unique.AsSpan(0, _uniqueCount);

        for (int i = 0; i < _uniqueCount; i++)
        {
            uint key = _unique[i];
            _seen[key >> 6] &= ~(1UL << (int)(key & 63));
        }
        _uniqueCount = 0;
        _count = sprites.Length;
        for (int i = 0; i < sprites.Length; i++)
        {
            uint key = _sequence[i] = Key(in sprites[i]);
            ref ulong bits = ref _seen[key >> 6];
            ulong bit = 1UL << (int)(key & 63);
            if ((bits & bit) != 0) continue;
            bits |= bit;
            _unique[_uniqueCount++] = key;
        }
        return _unique.AsSpan(0, _uniqueCount);
    }

    // A missing page needs the original per-instance request accounting. Decide before touching anything so the
    // fallback cannot double-count requests. Recheck physical residency even when the logical page sequence matches.
    public bool TouchResident(ReadOnlySpan<SpriteInstance> sprites, PageResidency art, PageResidency anim,
        int animBase, long frame, bool enabled)
    {
        if (!enabled) return false;
        var keys = Update(sprites);
        foreach (uint key in keys)
        {
            int page = (int)(key & 0xFFFF);
            if ((key & 0x10000) == 0 ? !art.IsResident(page) : anim != null && !anim.IsResident(page - animBase))
                return false;
        }
        foreach (uint key in keys)
        {
            int page = (int)(key & 0xFFFF);
            if ((key & 0x10000) != 0) anim?.Touch(page - animBase, frame);
            else art.Touch(page, frame);
        }
        return true;
    }
}
