using System.Diagnostics;
using Friflo.Engine.ECS;
using Uo.Net;

namespace Uo.Client;

/// <summary>Receives player movement events from the sink (implemented by <see cref="Walker"/>).</summary>
public interface IPlayerMovement
{
    void OnServerPosition(ushort x, ushort y, sbyte z, Direction dir);
    void OnMoveResult(in MoveResult result);
    void OnMoveSequenceReset(MoveResetReason reason, int droppedPending);
}

/// <summary>Uo.Net → Friflo: writes the decoded entity stream into an <see cref="EntityStore"/>. Mobiles, ground items
/// and equipment are entities; equipment links to its mobile with <see cref="EquippedOn"/>. The static world is not in
/// the ECS. Single-threaded: driven by <see cref="IGameClient.Poll"/> on the main thread.</summary>
public sealed class EcsWorld : WorldSinkBase, IDynamicBlockers
{
    public static readonly long WalkTicks = Stopwatch.Frequency * 400 / 1000, RunTicks = Stopwatch.Frequency * 200 / 1000;

    public readonly EntityStore Store;
    readonly Dictionary<uint, Entity> _bySerial = new(16384);
    readonly List<Entity> _scratch = new(256);
    readonly ArchetypeQuery<Position, Graphic> _groundItems;
    public readonly Clock Clock;

    public IPlayerMovement Movement;
    public uint PlayerSerial;
    public Entity Player;
    public byte Map;
    public bool InWorld;
    public string LastState = "";
    public int Mobiles, Items, Removes, Packets;

    public EcsWorld(Clock clock)
    {
        EcsSchema.Register();
        Clock = clock;
        Store = new EntityStore();
        _groundItems = Store.Query<Position, Graphic>().AllTags(Tags.Get<ItemTag, OnGround>());
    }

    public bool TryGet(uint serial, out Entity e) => _bySerial.TryGetValue(serial, out e);
    public int EntityCount => _bySerial.Count;

    // ------------------------------------------------------------------ IWorldSink

    public override void OnStateChanged(ClientState state, ReadOnlySpan<char> detail)
    {
        LastState = detail.IsEmpty ? state.ToString() : $"{state} ({detail.ToString()})";
        Console.WriteLine($"  net: {LastState}");
    }

    public override void OnLoginConfirm(in PlayerInfo p)
    {
        PlayerSerial = p.Serial;
        Player = UpsertMobile(new MobileState { Serial = p.Serial, Body = p.Body, X = p.X, Y = p.Y, Z = p.Z, Direction = p.Direction, IsPlayer = true, Source = MobileSource.Update20 });
        Movement?.OnServerPosition(p.X, p.Y, p.Z, p.Direction);
    }

    public override void OnLoginComplete() => InWorld = true;

    public override void OnMobile(in MobileState m, ReadOnlySpan<EquipEntry> equipment)
    {
        Packets++;
        var e = UpsertMobile(m);
        foreach (ref readonly var eq in equipment)
            UpsertEquip(eq.Serial, (ushort)(eq.ItemId & 0x7FFF), eq.Hue, eq.Layer, e);
    }

    Entity UpsertMobile(in MobileState m)
    {
        bool isPlayer = m.Serial == PlayerSerial && PlayerSerial != 0;
        var pos = new Position { X = m.X, Y = m.Y, Z = m.Z };
        if (!_bySerial.TryGetValue(m.Serial, out var e))
        {
            e = Store.CreateEntity(new Serial { Value = m.Serial }, new Graphic { Value = m.Body }, new Hue { Value = m.Hue }, pos,
                new Facing { Value = (byte)m.Direction }, new Flags { Value = m.Flags }, new Notoriety { Value = m.Notoriety }, new Motion(),
                isPlayer ? Tags.Get<MobileTag, PlayerTag>() : Tags.Get<MobileTag>());
            e.AddComponent(new Equipment());
            _bySerial[m.Serial] = e;
            Mobiles++;
            if (isPlayer) Player = e;
            return e;
        }

        e.GetComponent<Graphic>().Value = m.Body;
        e.GetComponent<Hue>().Value = m.Hue;
        e.GetComponent<Flags>().Value = m.Flags;
        if (m.Notoriety != 0) e.GetComponent<Notoriety>().Value = m.Notoriety;

        if (isPlayer)
        {
            // The walker owns the player's position; only a full update (0x20: teleport, resync) moves it.
            if (m.Source == MobileSource.Update20) Movement?.OnServerPosition(m.X, m.Y, m.Z, m.Direction);
            return e;
        }

        ref var p = ref e.GetComponent<Position>();
        ref var motion = ref e.GetComponent<Motion>();
        int dx = Math.Abs(m.X - p.X), dy = Math.Abs(m.Y - p.Y);
        if ((dx | dy) != 0 && dx <= 1 && dy <= 1 && m.Source == MobileSource.Moving77)
        {
            long now = Clock.Now;
            motion = new Motion { FromX = p.X, FromY = p.Y, FromZ = p.Z, Start = now, Duration = (m.Direction & Direction.Running) != 0 ? RunTicks : WalkTicks };
        }
        else if ((dx | dy) != 0) motion = default;
        p = pos;
        e.GetComponent<Facing>().Value = (byte)m.Direction;
        return e;
    }

    public override void OnItem(in ItemState item)
    {
        Packets++;
        var pos = new Position { X = item.X, Y = item.Y, Z = item.Z };
        var tags = item.Kind == ItemKind.Multi ? Tags.Get<ItemTag, OnGround, MultiTag>() : Tags.Get<ItemTag, OnGround>();
        if (!_bySerial.TryGetValue(item.Serial, out var e))
        {
            e = Store.CreateEntity(new Serial { Value = item.Serial }, new Graphic { Value = item.ItemId }, new Hue { Value = item.Hue }, pos,
                new Amount { Value = item.Amount }, new Flags { Value = item.Flags }, tags);
            _bySerial[item.Serial] = e;
            Items++;
            return;
        }
        if (e.HasComponent<EquippedOn>())
        {
            ClearSlot(e);
            e.RemoveComponent<EquippedOn>();
        }
        e.AddComponent(pos);
        e.AddComponent(new Amount { Value = item.Amount });
        e.AddComponent(new Flags { Value = item.Flags });
        e.GetComponent<Graphic>().Value = item.ItemId;
        e.GetComponent<Hue>().Value = item.Hue;
        e.AddTags(tags);
    }

    public override void OnEquip(in EquipState eq)
    {
        Packets++;
        if (!_bySerial.TryGetValue(eq.Parent, out var parent)) return; // parent not known (yet): ignored, as ClassicUO does
        UpsertEquip(eq.Serial, eq.ItemId, eq.Hue, eq.Layer, parent);
    }

    void UpsertEquip(uint serial, ushort itemId, ushort hue, byte layer, Entity parent)
    {
        var link = new EquippedOn { Parent = parent, Layer = layer };
        if (layer < Layers.Count && parent.HasComponent<Equipment>())
            parent.GetComponent<Equipment>().Slots[layer] = itemId | (uint)hue << 16;
        if (!_bySerial.TryGetValue(serial, out var e))
        {
            e = Store.CreateEntity(new Serial { Value = serial }, new Graphic { Value = itemId }, new Hue { Value = hue }, link, Tags.Get<ItemTag>());
            _bySerial[serial] = e;
            Items++;
            return;
        }
        if (e.HasComponent<EquippedOn>())
        {
            var old = e.GetComponent<EquippedOn>();
            if (old.Parent != parent || old.Layer != layer) ClearSlot(e);
        }
        e.GetComponent<Graphic>().Value = itemId;
        e.GetComponent<Hue>().Value = hue;
        e.AddComponent(link);
        if (e.HasComponent<Position>()) e.RemoveComponent<Position>();
        e.RemoveTags(Tags.Get<OnGround, MultiTag>());
    }

    /// <summary>Empties the parent's equipment slot that <paramref name="item"/> (an equipped item) occupies.</summary>
    static void ClearSlot(Entity item)
    {
        var link = item.GetComponent<EquippedOn>();
        var p = link.Parent;
        if (p.IsNull || link.Layer >= Layers.Count || !p.HasComponent<Equipment>()) return;
        ref var slot = ref p.GetComponent<Equipment>().Slots[link.Layer];
        if ((ushort)slot == item.GetComponent<Graphic>().Value) slot = 0;
    }

    public override void OnRemove(uint serial)
    {
        if (!_bySerial.Remove(serial, out var e)) return;
        Removes++;
        if (e.HasComponent<EquippedOn>()) ClearSlot(e);
        DeleteWithEquipment(e);
    }

    void DeleteWithEquipment(Entity e)
    {
        if (e.Tags.Has<MobileTag>())
        {
            _scratch.Clear();
            foreach (var link in e.GetIncomingLinks<EquippedOn>()) _scratch.Add(link.Entity);
            foreach (var item in _scratch)
            {
                _bySerial.Remove(item.GetComponent<Serial>().Value);
                item.DeleteEntity();
            }
        }
        e.DeleteEntity();
    }

    public override void OnMapChange(byte map)
    {
        if (map == Map && InWorld) return;
        Map = map;
        // everything but the player belongs to the old facet
        _scratch.Clear();
        foreach (var kv in _bySerial) if (kv.Key != PlayerSerial) _scratch.Add(kv.Value);
        foreach (var e in _scratch)
        {
            if (e.IsNull) continue;
            _bySerial.Remove(e.GetComponent<Serial>().Value);
            e.DeleteEntity();
        }
    }

    public override void OnMoveResult(in MoveResult result) => Movement?.OnMoveResult(result);

    public override void OnMoveSequenceReset(MoveResetReason reason, int droppedPending) => Movement?.OnMoveSequenceReset(reason, droppedPending);

    public override void OnMessage(uint serial, byte type, ushort hue, ReadOnlySpan<char> name, ReadOnlySpan<char> text)
    {
        if (serial == 0xFFFFFFFF || serial == 0) Console.WriteLine($"  msg: {text.ToString()}");
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Ground items at a tile, for walk checks. A linear scan over ground items: a few µs for thousands of
    /// items, a handful of calls per step. A tile index is the upgrade path if item counts grow.</summary>
    public int ItemsAt(int x, int y, Span<ushort> graphics, Span<sbyte> zs)
    {
        int n = 0;
        foreach (var (pos, gfx, _) in _groundItems.Chunks)
        {
            var ps = pos.Span;
            var gs = gfx.Span;
            for (int i = 0; i < ps.Length && n < graphics.Length; i++)
            {
                if (ps[i].X != x || ps[i].Y != y) continue;
                graphics[n] = gs[i].Value;
                zs[n] = ps[i].Z;
                n++;
            }
        }
        return n;
    }
}

/// <summary>Frame clock in Stopwatch ticks. Real time in the client; stepped manually by headless tests.</summary>
public sealed class Clock
{
    public long Now;
    public bool Manual;
    public void Tick() { if (!Manual) Now = Stopwatch.GetTimestamp(); }
    public static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);
}
