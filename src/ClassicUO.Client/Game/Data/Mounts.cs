using System.Collections.Generic;

namespace ClassicUO;

internal static class Mounts
{
    private static readonly Dictionary<ushort, MountInfo> _mounts = new();

    // One table for the client, the PoC client and the converter (rider shifts): Uo.Pack.MountTable.
    static Mounts()
    {
        foreach (var (item, body, offsetY) in Uo.Pack.MountTable.Entries)
        {
            _mounts[item] = new(body, item, offsetY);
        }
    }

    public static bool TryGet(ushort animId, out MountInfo mountInfo)
    {
        return _mounts.TryGetValue(animId, out mountInfo);
    }
}

internal readonly struct MountInfo
{
    public readonly ushort Graphic;
    public readonly ushort AnimationId;
    public readonly sbyte OffsetY;


    public MountInfo(ushort graphic, ushort animId, sbyte offsetY)
    {
        Graphic = graphic;
        AnimationId = animId;
        OffsetY = offsetY;
    }
}
