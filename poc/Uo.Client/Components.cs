using Friflo.Engine.ECS;

namespace Uo.Client;

public struct Serial : IComponent { public uint Value; }
/// <summary>Item id (items) or body id (mobiles).</summary>
public struct Graphic : IComponent { public ushort Value; }
public struct Hue : IComponent { public ushort Value; }
public struct Position : IComponent { public ushort X, Y; public sbyte Z; }
public struct Facing : IComponent { public byte Value; }   // Uo.Net.Direction incl. the running bit
public struct Flags : IComponent { public byte Value; }
public struct Notoriety : IComponent { public byte Value; }
public struct Amount : IComponent { public ushort Value; }

/// <summary>Equipment relation: the item is worn by <see cref="Parent"/> on <see cref="Layer"/>.
/// A link component, so <c>mobile.GetIncomingLinks&lt;EquippedOn&gt;()</c> lists a mobile's equipment.</summary>
public struct EquippedOn : ILinkComponent
{
    public Entity Parent;
    public byte Layer;
    public Entity GetIndexedValue() => Parent;
}

/// <summary>Step interpolation: the entity is moving from (FromX, FromY, FromZ) to its <see cref="Position"/>
/// between Start and Start + Duration (Stopwatch ticks). Done when now ≥ Start + Duration.</summary>
public struct Motion : IComponent
{
    public ushort FromX, FromY;
    public sbyte FromZ;
    public long Start, Duration;
}

public struct MobileTag : ITag { }
public struct ItemTag : ITag { }
public struct PlayerTag : ITag { }
/// <summary>Item lying in the world (not equipped, not in a container).</summary>
public struct OnGround : ITag { }
public struct MultiTag : ITag { }

public static class EcsSchema
{
    static EntitySchema _schema;

    /// <summary>NativeAOT has no reflection-based type discovery: register every component and tag before any EntityStore exists.</summary>
    public static void Register()
    {
        if (_schema != null) return;
        var aot = new NativeAOT();
        aot.RegisterComponent<Serial>(); aot.RegisterComponent<Graphic>(); aot.RegisterComponent<Hue>();
        aot.RegisterComponent<Position>(); aot.RegisterComponent<Facing>(); aot.RegisterComponent<Flags>();
        aot.RegisterComponent<Notoriety>(); aot.RegisterComponent<Amount>(); aot.RegisterComponent<Motion>();
        aot.RegisterIndexedComponentEntity<EquippedOn>();
        aot.RegisterTag<MobileTag>(); aot.RegisterTag<ItemTag>(); aot.RegisterTag<PlayerTag>();
        aot.RegisterTag<OnGround>(); aot.RegisterTag<MultiTag>();
        _schema = aot.CreateSchema();
    }
}
