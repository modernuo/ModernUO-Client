// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        public ushort Stamina, StaminaMax, Mana, ManaMax;
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

                // T3a: children attached under this serial before this record existed (a record replaced while its children
                // stayed attached) are worn on it; normally none (one index lookup)
                if (GetEquipment(serial).Count != 0)
                {
                    RebuildWorn(serial, ref e.GetComponent<EquipmentSlots>().Worn);
                }

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
            byte oldWornLayer = 0;
            ulong oldWornStamp = 0;

            bool created = !_bySerial.TryGetValue(serial, out Entity e) || !e.Tags.Has<ItemTag>();

            if (!created && e.TryGetComponent(out EquippedOn worn))
            {
                oldWearer = worn.Parent;
                oldWornLayer = worn.Layer;
                oldWornStamp = e.GetComponent<ItemData>().Stamp;
            }

            // T2: the contents cache follows attachment (only when some parent's contents are cached: no cost otherwise)
            bool track = _contents.Count != 0;
            ulong oldStamp = 0;
            uint oldParent = 0;

            if (track && !created)
            {
                oldStamp = oldWearer != 0 ? oldWornStamp : e.GetComponent<ItemData>().Stamp;

                if (oldStamp != 0)
                {
                    oldParent = AttachedParent(e);
                }
            }

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
            // (the record's stamp after the write, for T2 and T3a without reading it back)
            ulong stampNow = created && attach ? _childStamp : 0;

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

                stampNow = slot.Stamp;
            }

            // T2: a change of attachment (attached, detached, re-pushed, moved to another parent) changes the old parent's
            // and the new parent's contents
            if (track)
            {
                ulong newStamp = stampNow;

                if (newStamp != oldStamp)
                {
                    if (oldStamp != 0)
                    {
                        InvalidateContents(oldParent);
                    }

                    if (newStamp != 0)
                    {
                        InvalidateContents(container);
                    }
                }
            }

            // T3a: an attachment to a wearer that changed (attached, detached, re-pushed, another layer or wearer) moves the
            // item in the wearers' WornLayers
            bool wearer = IsWearerSerial(container);

            if (oldWearer != 0 || wearer)
            {
                UpdateWorn(serial, oldWearer, oldWornLayer, oldWornStamp, wearer ? container : 0, wearer ? layer : (byte)0, wearer ? stampNow : 0);
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
                _mobileEpoch++;
            }
            else
            {
                ItemCount--;

                if (e.TryGetComponent(out EquippedOn worn))
                {
                    MarkEquipmentDirty(worn.Parent);

                    // T3a: an attached worn item leaves its wearer's layer (the next one on that layer takes it)
                    if (e.GetComponent<ItemData>().Stamp != 0)
                    {
                        LeaveWorn(worn.Parent, worn.Layer, serial);
                    }
                }
            }

            // T2: an attached child leaves its parent's contents; a parent's own cached contents go with its record
            if (_contents.Count != 0)
            {
                if (e.TryGetComponent(out ItemData data) && data.Stamp != 0)
                {
                    InvalidateContents(AttachedParent(e));
                }

                DropContents(serial);
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
            _mobileEpoch++;

            // T2: no record is left, so no parent has contents
            foreach (ContentsSlot slot in _contents.Values)
            {
                ReleaseSlot(slot);
            }

            _contents.Clear();

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

            ref ItemData data = ref e.GetComponent<ItemData>();
            ulong oldStamp = data.Stamp;
            data.Stamp = ++_childStamp;

            // T3a: a worn item went to the back of its wearer's list (another item on its layer may now come first)
            if (e.TryGetComponent(out EquippedOn worn))
            {
                if (oldStamp != 0)
                {
                    LeaveWorn(worn.Parent, worn.Layer, serial);
                }

                EnterWorn(worn.Parent, worn.Layer, serial, _childStamp);

                // Codex audit P2 (#1): the wearer's EquipmentSlots are built from WornLayers; a re-order can change a
                // layer's first item, so the slots rebuild (the normal dirty dispatch, not only an explicit Rebuild)
                MarkEquipmentDirty(worn.Parent);
            }

            // T2: the parent's order changed (or, from 0, the item joined its contents)
            if (_contents.Count != 0)
            {
                InvalidateContents(AttachedParent(e));
            }

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

            ref ItemData data = ref e.GetComponent<ItemData>();

            if (data.Stamp != 0 && _contents.Count != 0)
            {
                InvalidateContents(AttachedParent(e));
            }

            ulong oldStamp = data.Stamp;
            data.Stamp = 0;

            // T3a: a detached worn item (the re-key's displaced mobile) leaves its wearer's layer
            if (oldStamp != 0 && e.TryGetComponent(out EquippedOn worn))
            {
                LeaveWorn(worn.Parent, worn.Layer, serial);

                // Codex audit P2 (#1): the item left the wearer's worn set, so its EquipmentSlots rebuild
                MarkEquipmentDirty(worn.Parent);
            }

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

        /// <summary>T4: a mobile's stamina pool (0xA3, 0x2D, the player's 0x11), in <see cref="Vitals"/>.</summary>
        public bool SetStamina(uint serial, ushort staminaMax, ushort stamina)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            if (e.HasComponent<Vitals>())
            {
                ref Vitals v = ref e.GetComponent<Vitals>();
                v.StaminaMax = staminaMax;
                v.Stamina = stamina;
            }
            else if (stamina != 0 || staminaMax != 0)
            {
                e.AddComponent(new Vitals { Stamina = stamina, StaminaMax = staminaMax });
            }

            return true;
        }

        /// <summary>T4: a mobile's mana pool (0xA2, 0x2D, the player's 0x11), in <see cref="Vitals"/>.</summary>
        public bool SetMana(uint serial, ushort manaMax, ushort mana)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            if (e.HasComponent<Vitals>())
            {
                ref Vitals v = ref e.GetComponent<Vitals>();
                v.ManaMax = manaMax;
                v.Mana = mana;
            }
            else if (mana != 0 || manaMax != 0)
            {
                e.AddComponent(new Vitals { Mana = mana, ManaMax = manaMax });
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
                x.Stamina = vitals.Stamina;
                x.StaminaMax = vitals.StaminaMax;
                x.Mana = vitals.Mana;
                x.ManaMax = vitals.ManaMax;
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
        /// the new serial (the mobile keeps them; a record already under that serial takes them, defaults included). T4: the
        /// stamina and mana too (the getters read them from the record with ecs_owns_scalars on).
        /// </summary>
        public void CarryMobileExtras(in MirrorExtras x, uint serial)
        {
            SetName(serial, x.Name);
            SetVitals(serial, x.Hits, x.HitsMax);
            SetStamina(serial, x.StaminaMax, x.Stamina);
            SetMana(serial, x.ManaMax, x.Mana);
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

        /// <summary>T2: a spellbook's spell set from 0xBF.1B (bit i = spell i + 1); false when the serial has no record.</summary>
        public bool SetSpellbook(uint serial, ulong spells)
        {
            if (!_bySerial.TryGetValue(serial, out Entity e))
            {
                return false;
            }

            if (e.HasComponent<SpellbookContent>())
            {
                e.GetComponent<SpellbookContent>().Spells = spells;
            }
            else if (spells != 0)
            {
                e.AddComponent(new SpellbookContent { Spells = spells });
            }

            return true;
        }

        /// <summary>T2: a spellbook's spell set (0 when none, or no record).</summary>
        public ulong Spellbook(uint serial) =>
            _bySerial.TryGetValue(serial, out Entity e) && e.TryGetComponent(out SpellbookContent c) ? c.Spells : 0;

        // ---- T2: container contents. A parent's contents are its attached children (worn or contained, child-order stamp
        // != 0) in stamp order: ClassicUO's child list (arrival order, a re-push to the back; orphans and the spellbook's
        // detached entries are not in it). Friflo's index order is not stable across removes, so each parent's order is a
        // cached sorted array, built on the first read after a change and invalidated by every change of attachment
        // (UpsertItem's attach, detach and move, StampChild, DetachChild, Remove, Clear). Nothing is tracked until the
        // first read (no cached parent: the writes skip the invalidation).

        private sealed class ContentsSlot
        {
            public uint[] Serials = Array.Empty<uint>();
            public int Count;
            public bool Valid;
        }

        private readonly Dictionary<uint, ContentsSlot> _contents = new Dictionary<uint, ContentsSlot>(64);
        private readonly Stack<ContentsSlot> _freeSlots = new Stack<ContentsSlot>();
        private (ulong Stamp, uint Serial)[] _contentsScratch = new (ulong, uint)[64];

        /// <summary>T2, tests and stats: contents builds (a read after a change) and invalidations of a valid cached parent.</summary>
        public long ContentsBuilds, ContentsInvalidations;

        /// <summary>T2: parents whose contents are cached (valid or not).</summary>
        public int CachedContentsParents => _contents.Count;

        /// <summary>
        /// T2, the memory log: the bytes the contents cache holds, by layout-based accounting of the 64-bit runtime (computed
        /// from the object layouts and capacities, not measured with a heap tool): the dictionary object 80 B, its buckets
        /// (24 B header + 4 B per slot of capacity, 8-aligned) and entries (24 B header + 24 B per slot), and the
        /// ValueCollection 24 B that <c>.Values</c> creates once and the dictionary keeps; each slot (cached or free) 32 B
        /// with its uint[] 24 B + 4 B per element (8-aligned); the free-slot stack 32 B with its array 24 B + 8 B per slot;
        /// the sort scratch 24 B + 16 B per entry (Codex audit round 2: alignment and the ValueCollection were missing).
        /// </summary>
        public long ContentsCacheBytes
        {
            get
            {
                long bytes = DictionaryBytes(_contents.EnsureCapacity(0), 24);
                bytes += 32 + 24 + 8L * _freeSlotsCapacity;
                bytes += 24 + 16L * _contentsScratch.Length;

                foreach (ContentsSlot slot in _contents.Values)
                {
                    bytes += SlotBytes(slot);
                }

                foreach (ContentsSlot slot in _freeSlots)
                {
                    bytes += SlotBytes(slot);
                }

                return bytes;
            }
        }

        /// <summary>
        /// T2, layout-based accounting of a <c>Dictionary</c> enumerated through <c>.Values</c> (64-bit): the object 80 B, the
        /// buckets int[] (24 B header + 4 B per slot of capacity, 8-aligned), the entries array (24 B header + the entry size
        /// per slot) and the cached ValueCollection 24 B (the enumeration allocates it once; the dictionary keeps it).
        /// </summary>
        public static long DictionaryBytes(int capacity, int entryBytes) =>
            80 + 24 + (capacity == 0 ? 0 : ((24 + 4L * capacity + 7) & ~7L) + 24 + (long)entryBytes * capacity);

        private static long SlotBytes(ContentsSlot slot) => 32 + (slot.Serials.Length == 0 ? 0 : (24 + 4L * slot.Serials.Length + 7) & ~7L);

        // the free-slot stack's array length (Stack<T> doubles from 4; it never shrinks)
        private int _freeSlotsCapacity;

        /// <summary>
        /// T2: the parent's attached children (serials), in child order (the stamp order). The span is valid until the next
        /// change of this parent's contents: a reader must not change them while it walks the span. No allocation once the
        /// parent's array is large enough.
        /// </summary>
        public ReadOnlySpan<uint> Contents(uint parent)
        {
            if (!_contents.TryGetValue(parent, out ContentsSlot slot))
            {
                slot = _freeSlots.Count != 0 ? _freeSlots.Pop() : new ContentsSlot();
                _contents.Add(parent, slot);
            }

            if (!slot.Valid)
            {
                BuildContents(parent, slot);
            }

            return new ReadOnlySpan<uint>(slot.Serials, 0, slot.Count);
        }

        private void BuildContents(uint parent, ContentsSlot slot)
        {
            ContentsBuilds++;
            int n = 0;

            foreach (Entity e in GetContents(parent))
            {
                AddAttached(e, ref n);
            }

            foreach (Entity e in GetEquipment(parent))
            {
                AddAttached(e, ref n);
            }

            // stamps are unique (one counter), so the order is total
            Span<(ulong Stamp, uint Serial)> found = _contentsScratch.AsSpan(0, n);
            found.Sort(static (a, b) => a.Stamp.CompareTo(b.Stamp));

            if (slot.Serials.Length < n)
            {
                slot.Serials = new uint[Math.Max(n, Math.Max(4, slot.Serials.Length * 2))];
            }

            for (int i = 0; i < n; i++)
            {
                slot.Serials[i] = found[i].Serial;
            }

            slot.Count = n;
            slot.Valid = true;
        }

        private void AddAttached(Entity e, ref int n)
        {
            ulong stamp = e.GetComponent<ItemData>().Stamp;

            if (stamp == 0)
            {
                return;
            }

            if (n == _contentsScratch.Length)
            {
                Array.Resize(ref _contentsScratch, n * 2);
            }

            _contentsScratch[n++] = (stamp, e.GetComponent<Serial>().Value);
        }

        private void InvalidateContents(uint parent)
        {
            if (_contents.TryGetValue(parent, out ContentsSlot slot) && slot.Valid)
            {
                slot.Valid = false;
                ContentsInvalidations++;
            }
        }

        private void DropContents(uint parent)
        {
            if (_contents.Remove(parent, out ContentsSlot slot))
            {
                ReleaseSlot(slot);
            }
        }

        private void ReleaseSlot(ContentsSlot slot)
        {
            slot.Valid = false;
            slot.Count = 0;

            // a few free slots keep their arrays for the next parent read (containers opened and closed)
            if (_freeSlots.Count < 64)
            {
                _freeSlots.Push(slot);
                _freeSlotsCapacity = Math.Max(_freeSlotsCapacity, _freeSlots.Count <= 4 ? 4 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)_freeSlots.Count));
            }
        }

        // the parent an attached item is in (its EquippedOn or ContainedIn; an attached item has one)
        // ---- T3a: WornLayers, a wearer's first attached item per layer (ecs-transition-plan.md, T3)

        /// <summary>WornLayers slot changes (tests, the 5 s summary).</summary>
        public long WornWrites, WornRescans;

        /// <summary>
        /// The serial of the first item attached to <paramref name="mobile"/> on <paramref name="layer"/> (the lowest
        /// child-order stamp: what <c>FindItemByLayer</c> finds first in the child list), 0 when none, when the layer is not
        /// tracked (0, above 0x1D) or when the serial has no mobile record.
        /// </summary>
        public uint Worn(uint mobile, byte layer) =>
            _bySerial.TryGetValue(mobile, out Entity e) && e.TryGetComponent(out EquipmentSlots eq) ? eq.Worn.Get(layer) : 0;

        /// <summary>
        /// Memory of WornLayers (layout-based accounting, as <see cref="ContentsCacheBytes"/>): the struct in each mobile record's
        /// EquipmentSlots (the serials, the per-layer counts and the 64-bit version: 160 B; Friflo stores a component in a per-archetype struct
        /// array, so the cost is the struct size per mobile record, plus the columns' spare capacity, not counted here).
        /// </summary>
        public long WornLayersBytes => (long)MobileCount * Unsafe.SizeOf<WornLayers>();

        /// <summary>Tests: the attached items on a wearer's layer, as WornLayers counts them.</summary>
        public int WornCount(uint mobile, byte layer) =>
            _bySerial.TryGetValue(mobile, out Entity e) && e.TryGetComponent(out EquipmentSlots eq) && WornLayers.Tracked(layer) ? eq.Worn.Counts[layer - 1] : 0;

        // the item's attachment to a wearer before and after a write (a wearer's parent, layer and stamp; 0 when none)
        private void UpdateWorn(uint serial, uint oldParent, byte oldLayer, ulong oldStamp, uint newParent, byte newLayer, ulong newStamp)
        {
            if (newParent == oldParent && newLayer == oldLayer && newStamp == oldStamp)
            {
                return;
            }

            if (oldStamp != 0)
            {
                LeaveWorn(oldParent, oldLayer, serial);
            }

            if (newStamp != 0)
            {
                EnterWorn(newParent, newLayer, serial, newStamp);
            }
        }

        // the wearer's record with WornLayers: a one-entry cache (an operation touches one wearer many times: 0x78's clear and
        // wears), valid while the serial is the same and no record was removed (the epoch); a new record for a serial only
        // follows a removal of the old one
        private Entity _wornParent;
        private uint _wornParentSerial, _wornParentEpoch;

        // bumped when a mobile record is deleted (Remove, Clear): the wearer cache's validity (an item's removal, which bumps
        // Epoch, leaves every wearer's record alive: 0x78's clear removes the items one by one, then the wears add them)
        private uint _mobileEpoch;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetWearer(uint parent, out Entity p)
        {
            if (_wornParentSerial == parent && _wornParentEpoch == _mobileEpoch && parent != 0)
            {
                p = _wornParent;

                return true;
            }

            if (!_bySerial.TryGetValue(parent, out p) || !p.HasComponent<EquipmentSlots>())
            {
                return false;
            }

            _wornParent = p;
            _wornParentSerial = parent;
            _wornParentEpoch = _mobileEpoch;

            return true;
        }

        // a slot changed: the mobile's version (a never-reused 64-bit value, so a reader's cache keyed on it can't see an old one)
        private void Bump(ref WornLayers w)
        {
            WornWrites++;
            w.Version = (ulong)WornWrites; // Codex audit P3 (#6): 64-bit end to end, never wraps
        }

        // the item joined the wearer's list on the layer with this stamp: it comes first unless an earlier one is there
        private void EnterWorn(uint parent, byte layer, uint serial, ulong stamp)
        {
            if (!WornLayers.Tracked(layer) || !TryGetWearer(parent, out Entity p))
            {
                return;
            }

            ref WornLayers w = ref p.GetComponent<EquipmentSlots>().Worn;
            ref byte count = ref w.Counts[layer - 1];

            // Codex audit P2 (#3): the count saturates (255 = 255 or more) instead of wrapping to 0; a departure then rescans
            if (count != WornLayers.SATURATED)
            {
                count++;
            }

            ref uint slot = ref w.Serials[layer - 1];

            if (slot == serial)
            {
                return;
            }

            if (slot == 0 || StampOf(slot) > stamp)
            {
                slot = serial;
                Bump(ref w);
            }
        }

        // the item left the wearer's list on the layer: when it was first, the next attached item on the layer (if any) is;
        // the layer's count of attached items spares the index scan when none is left
        private void LeaveWorn(uint parent, byte layer, uint serial)
        {
            if (!WornLayers.Tracked(layer) || !TryGetWearer(parent, out Entity p))
            {
                return;
            }

            ref WornLayers w = ref p.GetComponent<EquipmentSlots>().Worn;
            ref byte count = ref w.Counts[layer - 1];
            ref uint slot = ref w.Serials[layer - 1];

            if (count == WornLayers.SATURATED)
            {
                // Codex audit P2 (#3): the count is 255 or more, so the exact number left is not known: one rescan of the
                // wearer's equipment counts what is left on the layer (exact again below 255) and finds its first item
                count = (byte)Math.Min(ScanWorn(parent, layer, serial, out uint first), WornLayers.SATURATED);

                if (slot == serial)
                {
                    slot = first;
                    Bump(ref w);
                }

                return;
            }

            if (count != 0)
            {
                count--;
            }

            if (slot == serial)
            {
                slot = count == 0 ? 0 : FirstWorn(parent, layer, serial);
                Bump(ref w);
            }
        }

        // the attached child of the wearer on the layer with the lowest stamp, other than exclude (0 when none)
        private uint FirstWorn(uint parent, byte layer, uint exclude)
        {
            ScanWorn(parent, layer, exclude, out uint first);

            return first;
        }

        // the attached children of the wearer on the layer other than exclude: their number, and the one with the lowest stamp
        // (0 when none)
        private int ScanWorn(uint parent, byte layer, uint exclude, out uint first)
        {
            WornRescans++;
            int n = 0;
            uint best = 0;
            ulong bestStamp = ulong.MaxValue;

            foreach (Entity c in GetEquipment(parent))
            {
                if (c.GetComponent<EquippedOn>().Layer != layer)
                {
                    continue;
                }

                ulong stamp = c.GetComponent<ItemData>().Stamp;
                uint s = c.GetComponent<Serial>().Value;

                if (stamp == 0 || s == exclude)
                {
                    continue;
                }

                n++;

                if (stamp < bestStamp)
                {
                    best = s;
                    bestStamp = stamp;
                }
            }

            first = best;

            return n;
        }

        // every tracked layer from the wearer's attached children (a record created under a serial that has some)
        private void RebuildWorn(uint parent, ref WornLayers w)
        {
            w = default;
            Span<ulong> stamps = stackalloc ulong[WornLayers.COUNT];
            stamps.Clear();

            foreach (Entity c in GetEquipment(parent))
            {
                byte layer = c.GetComponent<EquippedOn>().Layer;
                ulong stamp = c.GetComponent<ItemData>().Stamp;

                if (stamp == 0 || !WornLayers.Tracked(layer))
                {
                    continue;
                }

                if (w.Counts[layer - 1] != WornLayers.SATURATED)
                {
                    w.Counts[layer - 1]++;
                }

                if (w.Serials[layer - 1] == 0 || stamp < stamps[layer - 1])
                {
                    w.Serials[layer - 1] = c.GetComponent<Serial>().Value;
                    stamps[layer - 1] = stamp;
                }
            }

            Bump(ref w);
        }

        private ulong StampOf(uint serial) =>
            _bySerial.TryGetValue(serial, out Entity e) && e.TryGetComponent(out ItemData d) ? d.Stamp : ulong.MaxValue;

        private static uint AttachedParent(Entity e)
        {
            if (e.TryGetComponent(out EquippedOn eq))
            {
                return eq.Parent;
            }

            return e.TryGetComponent(out ContainedIn ci) ? ci.Parent : NO_CONTAINER;
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
