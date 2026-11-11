// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using static ClassicUO.Network.NetClient;

namespace ClassicUO.Game.GameObjects
{
    enum HitsRequestStatus
    {
        None,
        Pending,
        Received
    }

    internal abstract class Entity : GameObject, IEquatable<Entity>
    {
        private static readonly RenderedText[] _hitsPercText = new RenderedText[101];


        protected Entity(World world, uint serial) : base(world)
        {
            Serial = serial;
        }

        /// <summary>
        /// E5 (mobiles) and T4 (every entity): the mirror entity last found for <see cref="EcsEntitySerial"/>, so a read skips
        /// the serial dictionary. Valid while <see cref="EcsEntityEpoch"/> is the mirror's <c>Epoch</c> (no record was deleted
        /// since: a deletion, the death re-key and the map clear all bump it) and the serial is the entity's own; 0 = none.
        /// The Gpu world's E5 draw writes it for the mobiles it draws without the identity check, so the scalar reads trust
        /// it only while <see cref="EcsEntityHeld"/> (set by their own lookup, which checks World holds this instance).
        /// </summary>
        internal Friflo.Engine.ECS.Entity EcsEntity;
        internal uint EcsEntitySerial, EcsEntityEpoch;
        internal bool EcsEntityHeld;

        /// <summary>
        /// T4 (ecs_owns_scalars): this entity's mirror record, when World holds this instance (not destroyed, the instance
        /// under its serial in <c>World.Items</c>/<c>World.Mobiles</c>) and the mirror has a record for its serial. False for
        /// a destroyed or stale instance (a reused serial's old object), a detached one (the character-creation preview, a
        /// player not in World yet) and a serial without a record: the getters then return the defaults (null, 0; T9 removed
        /// the caches). A miss is not cached (a later record creation does not bump the epoch).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryScalarEntity(out Friflo.Engine.ECS.Entity e)
        {
            e = EcsEntity;

            return EcsEntityHeld && EcsEntitySerial == _serial && EcsEntityEpoch == World.Ecs.Mirror.Epoch && !IsDestroyed || FindScalarEntity(out e);
        }

        /// <summary>
        /// T9 S5: the record the record-only getters read: <see cref="TryScalarEntity"/>'s world record or, for a detached
        /// preview object (the character-creation and race-change gumps'), its preview record (<c>WorldEcs.Previews</c>).
        /// Only the getters use it: the systems and extracts that call <see cref="TryScalarEntity"/> stay world-only.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryRecord(out Friflo.Engine.ECS.Entity e) => TryScalarEntity(out e) || FindPreviewRecord(out e);

        /// <summary>
        /// T9 S8c: the record the Mobile's timer and step writes go to: the world record only when its handle is this instance
        /// (checked, not the cached handle: a replaced instance's cache can still name the record its successor took over
        /// without an epoch change), else its preview record. Writes are rare next to the reads; the reads use
        /// <see cref="TryRecord"/>.
        /// </summary>
        internal bool TryOwnRecord(out Friflo.Engine.ECS.Entity e)
        {
            e = default;

            if (IsDestroyed || World?.Ecs == null)
            {
                return false;
            }

            return _serial != 0 && World.TryGetLiveRecord(this, _serial, out e) || FindPreviewRecord(out e);
        }

        // previews are rare: no handle caching (the preview mirror has its own epoch)
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool FindPreviewRecord(out Friflo.Engine.ECS.Entity e)
        {
            e = default;

            return !IsDestroyed && World?.Ecs != null && World.Ecs.TryGetPreviewRecord(this, out e);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool FindScalarEntity(out Friflo.Engine.ECS.Entity e)
        {
            e = default;

            if (IsDestroyed || _serial == 0 || World?.Ecs == null)
            {
                return false;
            }

            if (!World.TryGetLiveRecord(this, _serial, out e))
            {
                return false;
            }

            EcsEntity = e;
            EcsEntitySerial = _serial;
            EcsEntityEpoch = World.Ecs.Mirror.Epoch;
            EcsEntityHeld = true;

            return true;
        }

        // T9 S6: Flags and Direction live only in the record (Flags, Facing: every record has both); WorldWriter's operations
        // commit them. Without one (an item before its first commit, the house placement preview, a destroyed or stale
        // instance, the detached player): None and North, the values a new object had
        public Flags Flags
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => TryRecord(out Friflo.Engine.ECS.Entity e) ? (Flags)EcsScalars.FlagBits(e) : Flags.None;
        }

        // T9 S9: Graphic and Hue live only in the record (Graphic, Hue: every record has both); WorldWriter's operations commit
        // them. Without one (an item before its first commit, a destroyed or stale instance, the detached player): 0, the
        // value a new object had; a spellbook's spell entry (no record) has the scroll graphic FillSpellbook gave it.
        // GameObject.Graphic/Hue dispatch here for items and mobiles
        internal ushort RecordGraphic
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => TryRecord(out Friflo.Engine.ECS.Entity e) ? EcsScalars.Graphic(e) : RecordlessGraphic();
        }

        internal const ushort SPELL_ENTRY_GRAPHIC = 0x1F2E;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ushort RecordlessGraphic() =>
            this is Item item && !IsDestroyed && World != null && World.State.IsSpellEntry(item, out _) ? SPELL_ENTRY_GRAPHIC : (ushort)0;


        internal ushort RecordHue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => TryRecord(out Friflo.Engine.ECS.Entity e) ? EcsScalars.Hue(e) : (ushort)0;
        }

        /// <summary>
        /// T9 S9: what the removed Graphic setter did besides its field write, for <see cref="WorldWriter"/>'s item operations
        /// to call after committing the record: a placed item whose graphic changed in place notes its cell's content change
        /// (its tiledata flags are an input of the max-Z decision; the setter skipped mobiles).
        /// </summary>
        internal void GraphicCommitted(ushort before)
        {
            if (this is not Mobile && TileChunk != null && Graphic != before)
            {
                TileChunk.OwnerMap?.NoteContentChanged(TileChunk.X, TileChunk.Y);
            }
        }

        public byte HitsPercentage;

        // T9 (cold-caches): Hits, HitsMax and Name live only in the mirror record; WorldWriter's hits operations commit them and
        // set HitsPercentage from the values they write (RefreshHitsPercentage)
        public ushort Hits
        {
            get
            {
                ScalarReads.Count(ScalarField.Hits);

                return TryScalarEntity(out Friflo.Engine.ECS.Entity e) ? EcsScalars.Hits(e) : (ushort)0;
            }
        }

        public ushort HitsMax
        {
            get
            {
                ScalarReads.Count(ScalarField.HitsMax);

                return TryScalarEntity(out Friflo.Engine.ECS.Entity e) ? EcsScalars.HitsMax(e) : (ushort)0;
            }
        }
        public bool IsClicked;
        private uint _serial;
        public string Name
        {
            get
            {
                ScalarReads.Count(ScalarField.Name);

                return TryScalarEntity(out Friflo.Engine.ECS.Entity e) ? EcsScalars.Name(e) : null;
            }
        }

        public uint Serial { get => _serial; set { FunnelCheck.WriteSerial(this, value); _serial = value; } }
        public HitsRequestStatus HitsRequest;


        public bool IsHidden => (Flags & Flags.Hidden) != 0;

        public Direction Direction
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => TryRecord(out Friflo.Engine.ECS.Entity e) ? (Direction)EcsScalars.Facing(e) : Direction.North;
        }

        /// <summary>
        /// T9 S6: what the removed Direction setter did after its field write, for <see cref="WorldWriter"/>'s operations to
        /// call where they wrote it, after committing the record: the direction-change hook (the player's door check) when the
        /// committed direction differs from <paramref name="before"/>, then the Sync (folded inside a folding operation;
        /// for an unfolded mobile it publishes the inputs, as the setter's Sync did).
        /// </summary>
        internal void DirectionCommitted(Direction before)
        {
            if (Direction != before)
            {
                OnDirectionChanged();
            }

            World.Ecs.Sync(this);
        }

        public bool Exists => World.Contains(Serial);

        public RenderedText HitsTexture => _hitsPercText[HitsPercentage % _hitsPercText.Length];


        public bool Equals(Entity e)
        {
            return e != null && Serial == e.Serial;
        }

        /// <summary>T1: the hue an entity keeps for a packet hue (pure; the funnel writes the result itself).</summary>
        internal static ushort FixedHue(ushort hue)
        {
            ushort fixedColor = (ushort) (hue & 0x3FFF);

            if (fixedColor != 0)
            {
                if (fixedColor >= 0x0BB8)
                {
                    fixedColor = 1;
                }

                fixedColor |= (ushort) (hue & 0xC000);
            }
            else
            {
                fixedColor = (ushort) (hue & 0x8000);
            }

            return fixedColor;
        }

        public void UpdateHits(byte perc)
        {
            if (perc != HitsPercentage)
            {
                HitsPercentage = perc;

                ref var rtext = ref _hitsPercText[perc % _hitsPercText.Length];

                if (rtext == null || rtext.IsDestroyed)
                {
                    ushort color = 0x0044;

                    if (perc < 30)
                    {
                        color = 0x0021;
                    }
                    else if (perc < 50)
                    {
                        color = 0x0030;
                    }
                    else if (perc < 80)
                    {
                        color = 0x0058;
                    }

                    rtext = RenderedText.Create($"[{perc}%]", color, 3, false);
                }
            }
        }

        public virtual void CheckGraphicChange(byte animIndex = 0)
        {
        }

        public override void Update()
        {
            base.Update();

            if (ObjectHandlesStatus == ObjectHandlesStatus.OPEN)
            {
                ObjectHandlesStatus = ObjectHandlesStatus.DISPLAYING;

                // TODO: Some servers may not want to receive this (causing original client to not send it),
                //but all servers tested (latest POL, old POL, ServUO, Outlands) do.
                if ( /*Client.Game.UO.Version > ClientVersion.CV_200 &&*/ SerialHelper.IsMobile(Serial))
                {
                    Socket.Send_NameRequest(Serial);
                }

                UIManager.Add(new NameOverheadGump(World, this));
            }
        }

        /// <summary>The per-frame rule Entity.Update applied: for HitsMax &gt; 0, the clamped percentage of Hits.</summary>
        public static byte HitsPercent(ushort hits, ushort hitsMax)
        {
            int perc = MathHelper.PercetangeOf(hits, hitsMax);

            return (byte)(perc > 100 ? 100 : perc < 0 ? 0 : perc);
        }

        /// <summary>
        /// T9 (cold-caches): the funnel's hits operations (0xA1, 0x2D, 0x11) call this with the values they commit, so the
        /// percentage and its text change with the hits and are never read back from the record. HitsMax 0 keeps the old one.
        /// </summary>
        internal void RefreshHitsPercentage(ushort hits, ushort hitsMax)
        {
            if (hitsMax > 0)
            {
                UpdateHits(HitsPercent(hits, hitsMax));
            }
        }

        public override void Destroy()
        {
            base.Destroy();

            GameActions.SendCloseStatus(World, Serial, HitsRequest >= HitsRequestStatus.Pending);

            // T9 S8c: a mobile's timer goes with its record (OnDestroyed); an item's frame stays on the Item
            if (this is Item destroyed)
            {
                destroyed.AnimIndex = 0;
                destroyed.LastAnimationChangeTime = 0;
            }

            World.Ecs.OnDestroyed(this);

            if (this is Item item)
            {
                World.OnItemDestroyed(item);
            }
            else if (this is Mobile mobile)
            {
                World.OnMobileDestroyed(mobile);
            }
        }

        public Item FindItem(ushort graphic, ushort hue = 0xFFFF) => FindItem(graphic, hue, Serial, 0);

        // T9 child-list slice B2: the walk does not come back to its root's serial below the root's own children (a mobile's
        // children may include the item worn under its serial), and stops at WorldState.MaxNestingDepth: should a loop get
        // into the serial graph (WorldWriter refuses one), it ends
        private Item FindItem(ushort graphic, ushort hue, uint root, int depth)
        {
            Item item = null;

            if (hue == 0xFFFF)
            {
                int minColor = 0xFFFF;

                // T2: the children from the ECS (WorldState.Children)
                foreach (Item it in World.State.Children(this))
                {

                    if (it.Graphic == graphic)
                    {
                        if (it.Hue < minColor)
                        {
                            item = it;
                            minColor = it.Hue;
                        }
                    }

                    if (SerialHelper.IsValid(it.Container) && depth < WorldState.MaxNestingDepth && (depth == 0 || it.Serial != root))
                    {
                        Item found = it.FindItem(graphic, hue, root, depth + 1);

                        if (found != null && found.Hue < minColor)
                        {
                            item = found;
                            minColor = found.Hue;
                        }
                    }
                }
            }
            else
            {
                // T2: the children from the ECS (WorldState.Children)
                foreach (Item it in World.State.Children(this))
                {

                    if (it.Graphic == graphic && it.Hue == hue)
                    {
                        item = it;
                    }

                    if (SerialHelper.IsValid(it.Container) && depth < WorldState.MaxNestingDepth && (depth == 0 || it.Serial != root))
                    {
                        Item found = it.FindItem(graphic, hue, root, depth + 1);

                        if (found != null)
                        {
                            item = found;
                        }
                    }
                }
            }

            return item;
        }

        public Item GetItemByGraphic(ushort graphic, bool deepsearch = false) => GetItemByGraphic(graphic, deepsearch, Serial, 0);

        // T9 child-list slice B2: bounded and not back to its root's serial, as FindItem
        private Item GetItemByGraphic(ushort graphic, bool deepsearch, uint root, int depth)
        {
            // T2: the children from the ECS (WorldState.Children)
            foreach (Item item in World.State.Children(this))
            {
                if (item.Graphic == graphic)
                {
                    return item;
                }

                // (legacy: the deep search walks this entity's children again, not the item's)
                if (deepsearch && depth < WorldState.MaxNestingDepth && World.State.HasChildren(item))
                {
                    foreach (Item childItem in World.State.Children(this))
                    {
                        if (depth != 0 && childItem.Serial == root)
                        {
                            continue;
                        }

                        Item res = childItem.GetItemByGraphic(graphic, deepsearch, root, depth + 1);

                        if (res != null)
                        {
                            return res;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The first item on <paramref name="layer"/> among the entity's children. T3a: for a mobile, the ECS's worn item
        /// (<see cref="WorldState.WornItem"/>: WornLayers). Every equipment reader goes through here: IsMounted, the animation
        /// group's hands, the abilities, the paperdoll, the layering rules, Legacy's MobileView. An item's (a corpse's laid
        /// equipment) is its first child on the layer in the ECS's child order (<see cref="WorldState.FindChild"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Item FindItemByLayer(Layer layer)
        {
            if (World?.State is WorldState state)
            {
                if (this is Mobile mobile)
                {
#if CUO_DEVTOOLS
                    FrameStats.Inc(FrameCounter.FindByLayer);
#endif
                    return state.WornItem(mobile, layer);
                }

                // T9 child-list slice A: an item's (a corpse's) children from the ECS
                return state.FindChild(this, layer);
            }

            return null;
        }

        public static implicit operator uint(Entity entity)
        {
            return entity.Serial;
        }

        public static bool operator ==(Entity e, Entity s)
        {
            return Equals(e, s);
        }

        public static bool operator !=(Entity e, Entity s)
        {
            return !Equals(e, s);
        }

        public override bool Equals(object obj)
        {
            return obj is Entity ent && Equals(ent);
        }

        public override int GetHashCode()
        {
            return (int) Serial;
        }


        public abstract ushort GetGraphicForAnimation();
    }
}