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
    /// <summary>-ecscheck v2 (T1 funnel part 9): the cold fields of a record (defaults when never written).</summary>
    public struct MirrorExtras
    {
        public string Name;
        public ushort Hits, HitsMax;
        public byte Notoriety, ItemLayer;
        public uint Price;

        /// <summary>The child-order stamp (0: not attached, in no parent's child list).</summary>
        public ulong ChildStamp;
    }

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

        /// <summary>E5: bumped whenever an entity is deleted (or the map cleared). A handle cached together with the epoch it
        /// was looked up at is still the serial's entity while the epoch is unchanged, with no reliance on Friflo's 16-bit
        /// revision (which wraps after 65,536 deletions of one id).</summary>
        public uint Epoch { get; private set; }

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

        /// <summary>Creates or updates a mobile's entity and returns it (the caller pushes <see cref="MobileInputs"/>).</summary>
        public Entity UpsertMobile(uint serial, ushort graphic, ushort hue, ushort x, ushort y, sbyte z, byte facing, byte flags, bool isPlayer)
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
                    new MobileInputs(),
                    isPlayer ? Tags.Get<MobileTag, PlayerTag, EquipmentDirty>() : Tags.Get<MobileTag, EquipmentDirty>()
                );

                _bySerial[serial] = e;
                MobileCount++;

                return e;
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

            return e;
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
            bool isMulti,
            bool attach = false
        )
        {
            uint oldWearer = 0;

            if (_bySerial.TryGetValue(serial, out Entity e) && e.Tags.Has<ItemTag>() && e.TryGetComponent(out EquippedOn worn))
            {
                oldWearer = worn.Parent;
            }

            bool created = e.IsNull || !e.Tags.Has<ItemTag>();

            if (created)
            {
                if (!e.IsNull)
                {
                    Remove(serial);
                }

                // T1 part 9: a new record is attached only by the operation's push (attach), with the next stamp
                e = Store.CreateEntity
                (
                    new Serial { Value = serial },
                    new Graphic { Value = graphic },
                    new Hue { Value = hue },
                    new Position { X = x, Y = y, Z = z },
                    new Facing { Value = facing },
                    new Flags { Value = flags },
                    new ItemData { Amount = amount, Layer = layer, Stamp = attach ? ++_childStamp : 0 },
                    Tags.Get<ItemTag>()
                );

                _bySerial[serial] = e;
                ItemCount++;
            }
            else
            {
                WriteCommon(e, graphic, hue, x, y, z, facing, flags);
            }

            bool moved = SetParent(e, container, layer);

            // an existing record (T1 part 9): attached (the operation's PushToBack) takes the next stamp; a new parent without
            // a push detaches (ClassicUO links an item only when its parent exists: an orphan stays out of the list);
            // otherwise it keeps its place. After SetParent: its component re-adds move the entity, so the ref is taken here
            if (!created)
            {
                ref ItemData slot = ref e.GetComponent<ItemData>();
                slot.Amount = amount;
                slot.Layer = layer;

                if (attach)
                {
                    slot.Stamp = ++_childStamp;
                }
                else if (moved)
                {
                    slot.Stamp = 0;
                }
            }

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

            Epoch++;
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
                Epoch++;
                e.DeleteEntity();
            }

            _scratch.Clear();
            _bySerial.Clear();
            MobileCount = 0;

            // no record is left, so no stamp is live: the counter starts over
            _childStamp = 0;
            ItemCount = 0;
        }

        private ulong _childStamp;

        /// <summary>The last child-order stamp taken (tests).</summary>
        public ulong ChildStamp => _childStamp;

        /// <summary>Tests: the next stamp is <paramref name="last"/> + 1 (the boundary tests).</summary>
        public void SetLastChildStampForTests(ulong last) => _childStamp = last;

        /// <summary>
        /// T1 part 9: an item went to the back of its parent's child list without a change of record (a re-order: the vendor
        /// sort's re-stamp in the new order, the old re-key's re-link): it takes the next stamp. False when the serial has no
        /// item record. (An operation that also changes the record stamps through <see cref="UpsertItem"/>'s attach.)
        /// </summary>
        public bool StampChild(uint serial)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e) || !e.HasComponent<ItemData>())
            {
                return false;
            }

            e.GetComponent<ItemData>().Stamp = ++_childStamp;

            return true;
        }

        /// <summary>
        /// T1 part 9: an item left its parent's child list but keeps its Container (the spellbook's list cleared by 0xBF.1B):
        /// not attached any more. False when the serial has no item record.
        /// </summary>
        public bool DetachChild(uint serial)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e) || !e.HasComponent<ItemData>())
            {
                return false;
            }

            e.GetComponent<ItemData>().Stamp = 0;

            return true;
        }

        /// <summary>The name of a record (the component is added on the first non-null name).</summary>
        public bool SetName(uint serial, string name)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            if (e.HasComponent<EntityName>())
            {
                e.GetComponent<EntityName>().Value = name;
            }
            else if (name != null)
            {
                e.AddComponent(new EntityName { Value = name });
            }

            return true;
        }

        public bool SetVitals(uint serial, ushort hits, ushort hitsMax)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            if (e.HasComponent<Vitals>())
            {
                ref Vitals v = ref e.GetComponent<Vitals>();
                v.Hits = hits;
                v.HitsMax = hitsMax;
            }
            else if (hits != 0 || hitsMax != 0)
            {
                e.AddComponent(new Vitals { Hits = hits, HitsMax = hitsMax });
            }

            return true;
        }

        public bool SetNotoriety(uint serial, byte notoriety)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            if (e.HasComponent<MobileNotoriety>())
            {
                e.GetComponent<MobileNotoriety>().Value = notoriety;
            }
            else if (notoriety != 0)
            {
                e.AddComponent(new MobileNotoriety { Value = notoriety });
            }

            return true;
        }

        public bool SetPrice(uint serial, uint price)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            if (e.HasComponent<ItemPrice>())
            {
                e.GetComponent<ItemPrice>().Value = price;
            }
            else if (price != 0)
            {
                e.AddComponent(new ItemPrice { Value = price });
            }

            return true;
        }

        /// <summary>-ecscheck v2: the cold fields of a record; false when the serial has no record.</summary>
        public bool TryGetExtras(uint serial, out MirrorExtras x)
        {
            x = default;

            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            if (e.TryGetComponent(out EntityName name))
            {
                x.Name = name.Value;
            }

            if (e.TryGetComponent(out Vitals vitals))
            {
                x.Hits = vitals.Hits;
                x.HitsMax = vitals.HitsMax;
            }

            if (e.TryGetComponent(out MobileNotoriety notoriety))
            {
                x.Notoriety = notoriety.Value;
            }

            if (e.TryGetComponent(out ItemPrice price))
            {
                x.Price = price.Value;
            }

            if (e.TryGetComponent(out ItemData slot))
            {
                x.ItemLayer = slot.Layer;
                x.ChildStamp = slot.Stamp;
            }

            return true;
        }

        /// <summary>
        /// The death re-key (T1 part 9): the name, hits and notoriety a mobile's old record had, written to its record under
        /// the new serial (the mobile keeps them; a record already under that serial takes them, defaults included).
        /// </summary>
        public void CarryMobileExtras(in MirrorExtras x, uint serial)
        {
            SetName(serial, x.Name);
            SetVitals(serial, x.Hits, x.HitsMax);
            SetNotoriety(serial, x.Notoriety);
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
                r.Amount = e.GetComponent<ItemData>().Amount;
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
        // anything else = inside that item. True when the parent changed (a layer change on the same wearer is not one).
        private static bool SetParent(Entity e, uint container, byte layer)
        {
            if (container == NO_CONTAINER || container == 0)
            {
                bool had = false;

                if (e.HasComponent<EquippedOn>())
                {
                    e.RemoveComponent<EquippedOn>();
                    had = true;
                }

                if (e.HasComponent<ContainedIn>())
                {
                    e.RemoveComponent<ContainedIn>();
                    had = true;
                }

                if (!e.Tags.Has<OnGround>())
                {
                    e.AddTag<OnGround>();
                }

                return had;
            }

            if (e.Tags.Has<OnGround>())
            {
                e.RemoveTag<OnGround>();
            }

            if (IsWearerSerial(container))
            {
                bool moved = false;

                if (e.HasComponent<ContainedIn>())
                {
                    e.RemoveComponent<ContainedIn>();
                    moved = true;
                }

                // indexed components must be re-added (not written by ref) so the index follows the new parent
                if (!e.TryGetComponent(out EquippedOn eq) || eq.Parent != container || eq.Layer != layer)
                {
                    moved |= eq.Parent != container;
                    e.AddComponent(new EquippedOn { Parent = container, Layer = layer });
                }

                return moved;
            }
            else
            {
                bool moved = false;

                if (e.HasComponent<EquippedOn>())
                {
                    e.RemoveComponent<EquippedOn>();
                    moved = true;
                }

                if (!e.TryGetComponent(out ContainedIn ci) || ci.Parent != container)
                {
                    e.AddComponent(new ContainedIn { Parent = container });
                    moved = true;
                }

                return moved;
            }
        }
    }
}
