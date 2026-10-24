using Friflo.Engine.ECS;

namespace EcsBench;

// ---- Components (shared by all three libraries) -------------------------------------------
// Friflo requires IComponent / ITag marker interfaces; TinyEcs and Arch ignore them.

public struct Pos : IComponent { public ushort X, Y; public sbyte Z; }
public struct Graphic : IComponent { public ushort Value; }
public struct Hue : IComponent { public ushort Value; }
public struct Depth : IComponent { public int Value; }
public struct Serial : IComponent { public uint Value; }
public struct Anim : IComponent { public byte Action, Frame; public ushort Timer; }
public struct Motion : IComponent { public float T, Speed; }
public struct Dir : IComponent { public byte Value; }
public struct Effect : IComponent { public ushort Life, Graphic; }
public struct Vel : IComponent { public float X, Y; }

// Tags. Arch and TinyEcs treat zero-size structs as tags.
public struct Land : ITag { }
public struct Static : ITag { }
public struct Item : ITag { }
public struct Mobile : ITag { }
public struct OnGround : ITag { }
public struct InRange : ITag { }
public struct Hidden : ITag { }
public struct Dirty : ITag { }

/// <summary>
/// Deterministic, library-independent description of the UO-shaped world. Every adapter
/// populates from the same arrays in the same order, so order-independent checksums
/// (sums/counts) must agree across libraries — a cheap correctness cross-check.
/// </summary>
public sealed class WorldSpec
{
    public const int MapW = 600, MapH = 500;              // 300k land tiles
    public const int Statics = 200_000;
    public const int Items = 50_000, ItemsOnGround = 40_000;
    public const int Mobiles = 5_000, HiddenMobiles = 250;
    public const int ViewMinX = 200, ViewMaxX = 264, ViewMinY = 200, ViewMaxY = 264;

    // Per-op workload sizes
    public const int Lookups = 20_000;
    public const int ToggleMobiles = 50, ToggleItems = 500;
    public const int EffectsPerFrame = 200, EffectLife = 30;
    public const int DirtyPerFrame = 500;
    public const int BlocksPerOp = 16, LandPerBlock = 64, StaticsPerBlock = 100;

    public readonly Pos[] StaticPos = new Pos[Statics];
    public readonly ushort[] StaticGfx = new ushort[Statics];
    public readonly Pos[] ItemPos = new Pos[Items];
    public readonly Pos[] MobilePos = new Pos[Mobiles];
    public readonly int[] LookupSerials = new int[Lookups]; // index into serial space [0, Items+Mobiles)

    public static readonly WorldSpec Instance = new();

    private WorldSpec()
    {
        var r = new Random(1);
        for (int i = 0; i < Statics; i++)
        {
            StaticPos[i] = new Pos { X = (ushort)r.Next(MapW), Y = (ushort)r.Next(MapH), Z = (sbyte)r.Next(-20, 60) };
            StaticGfx[i] = (ushort)r.Next(0x4000);
        }
        for (int i = 0; i < Items; i++)
            ItemPos[i] = new Pos { X = (ushort)r.Next(MapW), Y = (ushort)r.Next(MapH), Z = (sbyte)r.Next(0, 40) };
        for (int i = 0; i < Mobiles; i++)
            MobilePos[i] = new Pos { X = (ushort)r.Next(MapW), Y = (ushort)r.Next(MapH), Z = 0 };
        for (int i = 0; i < Lookups; i++)
            LookupSerials[i] = r.Next(Items + Mobiles);
    }

    public static Pos LandPos(int i) => new() { X = (ushort)(i % MapW), Y = (ushort)(i / MapW), Z = (sbyte)((i * 7) % 11) };
    public static uint ItemSerial(int i) => 0x4000_0000u + (uint)i;
    public static uint MobileSerial(int i) => (uint)(i + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeDepth(in Pos p, ushort gfx) => ((p.X + p.Y) << 4) + p.Z + (gfx & 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool InView(in Pos p) => p.X >= ViewMinX && p.X < ViewMaxX && p.Y >= ViewMinY && p.Y < ViewMaxY;
}

/// <summary>
/// One adapter instance owns one world. The harness creates a fresh adapter per benchmark,
/// calls <see cref="Populate"/>, then calls the benchmark op repeatedly. Ops return a checksum.
/// </summary>
public abstract class Adapter : IDisposable
{
    public abstract string Name { get; }
    public abstract void Populate();

    public abstract long RenderExtract();          // iterate ~555k Pos+Graphic+Depth, write depth, cull
    public abstract long RenderExtractParallel();  // same, multi-threaded (native API where available)
    public abstract long MobileTick();             // 5k mobiles: anim + motion -> pos
    public abstract long PacketLookups();          // 20k serial->entity random get/set
    public abstract long ChurnFrame();             // tag toggles + effect spawn/despawn + 6 small queries
    public abstract long ChurnManyQueries();       // same churn, 120 distinct live queries (real client: hundreds of systems)
    public abstract long BlockStream();            // spawn 16 map blocks, despawn 16 old blocks (~2.6k each)
    public abstract long DirtyTag();               // 500 hue changes flagged via tag, consumed, cleared
    public virtual long DirtyTicks() => throw new NotSupportedException(); // native per-row change ticks
    public abstract long Snapshot();               // copy mobile state out and back (rollback snapshot)

    // Rng state is advanced identically by every adapter.
    protected readonly Random Rng = new(42);
    protected int Frame;

    // Pre-allocated rollback buffers.
    protected readonly Pos[] SnapPos = new Pos[WorldSpec.Mobiles];
    protected readonly Motion[] SnapMotion = new Motion[WorldSpec.Mobiles];
    protected readonly Anim[] SnapAnim = new Anim[WorldSpec.Mobiles];

    public virtual void Dispose() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static void TickMobile(ref Pos p, ref Anim a, ref Motion m)
    {
        a.Timer += 16;
        if (a.Timer >= 100) { a.Timer -= 100; a.Frame = (byte)((a.Frame + 1) & 7); }
        m.T += m.Speed;
        if (m.T >= 1f) { m.T -= 1f; p.X = (ushort)((p.X + 1) % WorldSpec.MapW); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static Effect NewEffect(int i) => new() { Life = WorldSpec.EffectLife, Graphic = (ushort)(0x3700 + (i & 15)) };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static Pos EffectPos(int frame, int i) => new() { X = (ushort)((frame * 31 + i * 17) % WorldSpec.MapW), Y = (ushort)((frame * 13 + i * 7) % WorldSpec.MapH), Z = 10 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static Pos BlockLandPos(int blockSeq, int i)
    {
        var bx = (blockSeq * 8) % WorldSpec.MapW; var by = ((blockSeq * 8) / WorldSpec.MapW * 8) % WorldSpec.MapH;
        return new Pos { X = (ushort)(bx + (i & 7)), Y = (ushort)(by + (i >> 3)), Z = 0 };
    }
}
