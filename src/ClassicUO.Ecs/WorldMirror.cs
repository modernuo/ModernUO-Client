// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace ClassicUO.Ecs
{
    public enum ParentKind : byte
    {
        None,
        Equipped,
        Contained
    }

    public struct MirrorRecord
    {
        public uint Serial;
        public bool IsMobile, IsPlayer, OnGround, IsMulti;
        public ushort Graphic, Hue, X, Y, Amount;
        public sbyte Z;
        public byte Facing, Flags, Layer;
        public ParentKind ParentKind;
        public uint Parent;
    }

    // Friflo copy of the legacy World's items and mobiles, keyed by serial. Single-threaded, written by the World hooks.
    public sealed class WorldMirror
    {
        public const uint NO_CONTAINER = 0xFFFF_FFFF;

        private readonly Dictionary<uint, Entity> _bySerial = new Dictionary<uint, Entity>(4096);
        private readonly List<Entity> _scratch = new List<Entity>(256);

        public WorldMirror()
        {
            EcsSchema.Register();
            Store = new EntityStore();
        }

        public EntityStore Store { get; }

        public int Count => _bySerial.Count;

        public int MobileCount { get; private set; }

        public int ItemCount { get; private set; }

        public Dictionary<uint, Entity>.KeyCollection Serials => _bySerial.Keys;

        public static bool IsMobileSerial(uint serial) => serial > 0 && serial < 0x4000_0000;

        /// <summary>
        /// A parent serial that wears its items: a mobile, or a dying mobile that ClassicUO re-keyed to serial | 0x80000000
        /// for its death animation (PacketHandlers.DisplayDeath repoints its items there; MobileView still draws them).
        /// </summary>
        public static bool IsWearerSerial(uint serial) => IsMobileSerial(serial & 0x7FFF_FFFF);

        public bool TryGetEntity(uint serial, out Entity entity) => _bySerial.TryGetValue(serial, out entity);

        public void UpsertMobile(uint serial, ushort graphic, ushort hue, ushort x, ushort y, sbyte z, byte facing, byte flags, bool isPlayer)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e) || !e.Tags.Has<MobileTag>())
            {
                if (!e.IsNull)
                {
                    Remove(serial);
                }

                e = Store.CreateEntity
                (
                    new Serial { Value = serial },
                    new Graphic { Value = graphic },
                    new Hue { Value = hue },
                    new Position { X = x, Y = y, Z = z },
                    new Facing { Value = facing },
                    new Flags { Value = flags },
                    new AnimationState { ServerGroup = 0xFF },
                    new MobileRenderState { Alpha = 0xFF },
                    new EquipmentSlots(),
                    isPlayer ? Tags.Get<MobileTag, PlayerTag, EquipmentDirty>() : Tags.Get<MobileTag, EquipmentDirty>()
                );

                _bySerial[serial] = e;
                MobileCount++;

                return;
            }

            // the equipment's drawn bodies (equipconv, gargoyle remap, covered rules) depend on the body graphic
            if (e.GetComponent<Graphic>().Value != graphic)
            {
                MarkEquipmentDirty(e);
            }

            WriteCommon(e, graphic, hue, x, y, z, facing, flags);

            if (e.Tags.Has<PlayerTag>() != isPlayer)
            {
                if (isPlayer)
                {
                    e.AddTag<PlayerTag>();
                }
                else
                {
                    e.RemoveTag<PlayerTag>();
                }
            }
        }

        public void UpsertItem
        (
            uint serial,
            ushort graphic,
            ushort hue,
            ushort x,
            ushort y,
            sbyte z,
            byte facing,
            byte flags,
            ushort amount,
            uint container,
            byte layer,
            bool isMulti
        )
        {
            uint oldWearer = 0;

            if (_bySerial.TryGetValue(serial, out Entity e) && e.Tags.Has<ItemTag>() && e.TryGetComponent(out EquippedOn worn))
            {
                oldWearer = worn.Parent;
            }

            if (e.IsNull || !e.Tags.Has<ItemTag>())
            {
                if (!e.IsNull)
                {
                    Remove(serial);
                }

                e = Store.CreateEntity
                (
                    new Serial { Value = serial },
                    new Graphic { Value = graphic },
                    new Hue { Value = hue },
                    new Position { X = x, Y = y, Z = z },
                    new Facing { Value = facing },
                    new Flags { Value = flags },
                    new Amount { Value = amount },
                    Tags.Get<ItemTag>()
                );

                _bySerial[serial] = e;
                ItemCount++;
            }
            else
            {
                WriteCommon(e, graphic, hue, x, y, z, facing, flags);
                e.GetComponent<Amount>().Value = amount;
            }

            SetParent(e, container, layer);

            // any write to a worn item (equip, unequip, graphic, hue) invalidates the old and new wearer's slots
            if (oldWearer != 0)
            {
                MarkEquipmentDirty(oldWearer);
            }

            if (IsWearerSerial(container) && container != oldWearer)
            {
                MarkEquipmentDirty(container);
            }

            if (e.Tags.Has<MultiTag>() != isMulti)
            {
                if (isMulti)
                {
                    e.AddTag<MultiTag>();
                }
                else
                {
                    e.RemoveTag<MultiTag>();
                }
            }
        }

        public bool Remove(uint serial)
        {
            if (!_bySerial.Remove(serial, out Entity e))
            {
                return false;
            }

            if (e.Tags.Has<MobileTag>())
            {
                MobileCount--;
            }
            else
            {
                ItemCount--;

                if (e.TryGetComponent(out EquippedOn worn))
                {
                    MarkEquipmentDirty(worn.Parent);
                }
            }

            e.DeleteEntity();

            return true;
        }

        public void MarkEquipmentDirty(uint mobileSerial)
        {
            if (_bySerial.TryGetValue(mobileSerial, out Entity e))
            {
                MarkEquipmentDirty(e);
            }
        }

        private static void MarkEquipmentDirty(Entity e)
        {
            if (e.HasComponent<EquipmentSlots>() && !e.Tags.Has<EquipmentDirty>())
            {
                e.AddTag<EquipmentDirty>();
            }
        }

        public void Clear()
        {
            _scratch.Clear();

            foreach (Entity e in _bySerial.Values)
            {
                _scratch.Add(e);
            }

            foreach (Entity e in _scratch)
            {
                e.DeleteEntity();
            }

            _scratch.Clear();
            _bySerial.Clear();
            MobileCount = 0;
            ItemCount = 0;
        }

        public bool TryGet(uint serial, out MirrorRecord r)
        {
            r = default;

            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            r.Serial = e.GetComponent<Serial>().Value;
            r.IsMobile = e.Tags.Has<MobileTag>();
            r.IsPlayer = e.Tags.Has<PlayerTag>();
            r.OnGround = e.Tags.Has<OnGround>();
            r.IsMulti = e.Tags.Has<MultiTag>();
            r.Graphic = e.GetComponent<Graphic>().Value;
            r.Hue = e.GetComponent<Hue>().Value;
            ref Position p = ref e.GetComponent<Position>();
            r.X = p.X;
            r.Y = p.Y;
            r.Z = p.Z;
            r.Facing = e.GetComponent<Facing>().Value;
            r.Flags = e.GetComponent<Flags>().Value;

            if (!r.IsMobile)
            {
                r.Amount = e.GetComponent<Amount>().Value;
            }

            if (e.TryGetComponent(out EquippedOn eq))
            {
                r.ParentKind = ParentKind.Equipped;
                r.Parent = eq.Parent;
                r.Layer = eq.Layer;
            }
            else if (e.TryGetComponent(out ContainedIn ci))
            {
                r.ParentKind = ParentKind.Contained;
                r.Parent = ci.Parent;
            }
            else
            {
                r.Parent = NO_CONTAINER;
            }

            return true;
        }

        // Children of a mobile (equipment) or of a container item, by parent serial.
        public Entities GetEquipment(uint parent) => Store.ComponentIndex<EquippedOn, uint>()[parent];

        public Entities GetContents(uint parent) => Store.ComponentIndex<ContainedIn, uint>()[parent];

        private static void WriteCommon(Entity e, ushort graphic, ushort hue, ushort x, ushort y, sbyte z, byte facing, byte flags)
        {
            e.GetComponent<Graphic>().Value = graphic;
            e.GetComponent<Hue>().Value = hue;
            ref Position p = ref e.GetComponent<Position>();
            p.X = x;
            p.Y = y;
            p.Z = z;
            e.GetComponent<Facing>().Value = facing;
            e.GetComponent<Flags>().Value = flags;
        }

        // ClassicUO: 0xFFFFFFFF (or 0) = in the world; a mobile serial, or the 0x80000000 | serial of a dying mobile, = worn;
        // anything else = inside that item.
        private static void SetParent(Entity e, uint container, byte layer)
        {
            if (container == NO_CONTAINER || container == 0)
            {
                if (e.HasComponent<EquippedOn>())
                {
                    e.RemoveComponent<EquippedOn>();
                }

                if (e.HasComponent<ContainedIn>())
                {
                    e.RemoveComponent<ContainedIn>();
                }

                if (!e.Tags.Has<OnGround>())
                {
                    e.AddTag<OnGround>();
                }

                return;
            }

            if (e.Tags.Has<OnGround>())
            {
                e.RemoveTag<OnGround>();
            }

            if (IsWearerSerial(container))
            {
                if (e.HasComponent<ContainedIn>())
                {
                    e.RemoveComponent<ContainedIn>();
                }

                // indexed components must be re-added (not written by ref) so the index follows the new parent
                if (!e.TryGetComponent(out EquippedOn eq) || eq.Parent != container || eq.Layer != layer)
                {
                    e.AddComponent(new EquippedOn { Parent = container, Layer = layer });
                }
            }
            else
            {
                if (e.HasComponent<EquippedOn>())
                {
                    e.RemoveComponent<EquippedOn>();
                }

                if (!e.TryGetComponent(out ContainedIn ci) || ci.Parent != container)
                {
                    e.AddComponent(new ContainedIn { Parent = container });
                }
            }
        }
    }
}
