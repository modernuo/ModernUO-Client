// SPDX-License-Identifier: BSD-2-Clause

using Friflo.Engine.ECS;

namespace ClassicUO.Ecs
{
    public struct Serial : IComponent
    {
        public uint Value;
    }

    // Item id (items) or body id (mobiles).
    public struct Graphic : IComponent
    {
        public ushort Value;
    }

    public struct Hue : IComponent
    {
        public ushort Value;
    }

    public struct Position : IComponent
    {
        public ushort X, Y;
        public sbyte Z;
    }

    public struct Facing : IComponent
    {
        public byte Value;
    }

    public struct Flags : IComponent
    {
        public byte Value;
    }

    public struct Amount : IComponent
    {
        public ushort Value;
    }

    // Links are keyed by the parent's serial, not by Entity: ClassicUO accepts children before their
    // parent arrives, and a serial survives the parent being destroyed and recreated.
    public struct EquippedOn : IIndexedComponent<uint>
    {
        public uint Parent;
        public byte Layer;

        public uint GetIndexedValue() => Parent;
    }

    public struct ContainedIn : IIndexedComponent<uint>
    {
        public uint Parent;

        public uint GetIndexedValue() => Parent;
    }

    public struct MobileTag : ITag { }

    public struct ItemTag : ITag { }

    public struct PlayerTag : ITag { }

    // Item lying in the world: not equipped, not in a container.
    public struct OnGround : ITag { }

    public struct MultiTag : ITag { }

    public static class EcsSchema
    {
        private static readonly object _lock = new object();
        private static EntitySchema _schema;

        // NativeAOT has no reflection-based type discovery: register every component and tag before any EntityStore exists.
        public static void Register()
        {
            lock (_lock)
            {
                if (_schema == null)
                {
                    _schema = CreateSchema();
                }
            }
        }

        private static EntitySchema CreateSchema()
        {
            NativeAOT aot = new NativeAOT();
            aot.RegisterComponent<Serial>();
            aot.RegisterComponent<Graphic>();
            aot.RegisterComponent<Hue>();
            aot.RegisterComponent<Position>();
            aot.RegisterComponent<Facing>();
            aot.RegisterComponent<Flags>();
            aot.RegisterComponent<Amount>();
            aot.RegisterIndexedComponentStruct<EquippedOn, uint>();
            aot.RegisterIndexedComponentStruct<ContainedIn, uint>();
            aot.RegisterTag<MobileTag>();
            aot.RegisterTag<ItemTag>();
            aot.RegisterTag<PlayerTag>();
            aot.RegisterTag<OnGround>();
            aot.RegisterTag<MultiTag>();

            return aot.CreateSchema();
        }
    }
}
