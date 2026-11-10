// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using Microsoft.Xna.Framework;
using System;

namespace ClassicUO.Game.GameObjects
{
    // sealed: "is Item" is one type compare, not a walk up the class chain (the Legacy sink tests every drawn object)
    internal sealed partial class Item : Entity
    {
        //private static readonly QueuedPool<Item> _pool = new QueuedPool<Item>(
        //    Constants.PREDICTABLE_CHUNKS * 3,
        //    i =>
        //    {
        //        i.IsDestroyed = false;
        //        i.Graphic = 0;
        //        i.Amount = 0;
        //        i.Container = 0xFFFF_FFFF;
        //        i._isMulti = false;
        //        i.Layer = 0;
        //        i.Price = 0;
        //        i.UsedLayer = false;
        //        i._displayedGraphic = null;
        //        i.X = 0;
        //        i.Y = 0;
        //        i.Z = 0;

        //        i.LightID = 0;
        //        i.MultiDistanceBonus = 0;
        //        i.Flags = 0;
        //        i.WantUpdateMulti = true;
        //        i.MultiInfo = null;
        //        i.MultiGraphic = 0;

        //        i.AlphaHue = 0;
        //        i.Name = null;
        //        i.Direction = 0;
        //        i.AnimIndex = 0;
        //        i.Hits = 0;
        //        i.HitsMax = 0;
        //        i.LastStepTime = 0;
        //        i.LastAnimationChangeTime = 0;

        //        i.Clear();

        //        i.IsClicked = false;
        //        i.IsDamageable = false;
        //        i.Offset = Vector3.Zero;
        //        i.HitsPercentage = 0;
        //        i.Opened = false;
        //        i.TextContainer?.Clear();
        //        i.IsFlipped = false;
        //        i.FrameInfo = Rectangle.Empty;
        //        i.ObjectHandlesStatus = ObjectHandlesStatus.NONE;
        //        i.AlphaHue = 0;
        //        i.AllowedToDraw = true;
        //        i.ExecuteAnimation = true;
        //        i.HitsRequest = HitsRequestStatus.None;
        //    }
        //);

        private bool _isMulti;

        /// <summary>W5: listed in World's <c>ActiveItemList</c> (the list is the source with <c>ecs_active_items_source</c> off, the
        /// checker's shadow with it on). Read membership through <c>World.IsItemListed</c>.</summary>
        internal bool InUpdateList;

        /// <summary>T9 (<c>ecs_active_items_source</c>): the id of this item's <see cref="ClassicUO.Ecs.ActiveItemEntry"/> entity while it
        /// is listed, 0 otherwise (the O(1) membership test; an id, not an Entity, to keep Item 12 bytes smaller). Read membership
        /// through <c>World.IsItemListed</c>.</summary>
        internal int ActiveEntryId;

        /// <summary><see cref="ItemTable"/>'s held state: the table holding this item (null: none) and its key there, kept by the
        /// table's Add/Remove/Clear. <see cref="HeldShared"/>: once held under a second key or by a second table, ItemTable.Holds
        /// looks up instead. Read through <c>ItemTable.Holds</c>.</summary>
        internal ItemTable HeldBy;
        internal uint HeldKey;
        internal bool HeldShared;

        /// <summary>
        /// W5: whether <see cref="Update"/> does anything this frame. Item.Update is a no-op unless the item is a corpse
        /// (ProcessAnimation) or its object handles just opened (Entity.Update); the hits percentage follows the setters.
        /// </summary>
        internal bool NeedsFrameUpdate => !IsDestroyed && (IsCorpse || ObjectHandlesStatus == ObjectHandlesStatus.OPEN);

        public Item(World world) : base(world, 0) { }

        public bool IsCoin => Graphic == 0x0EEA || Graphic == 0x0EED || Graphic == 0x0EF0;

        public ushort DisplayedGraphic
        {
            get
            {
                if (IsCoin)
                {
                    if (Amount > 5)
                    {
                        return (ushort)(Graphic + 2);
                    }

                    if (Amount > 1)
                    {
                        return (ushort)(Graphic + 1);
                    }
                }
                else if (IsMulti)
                {
                    return MultiGraphic;
                }

                return Graphic;
            }
        }

        public bool IsLocked => (Flags & Flags.Movable) == 0 && ItemData.Weight > 90;

        public ushort MultiGraphic { get; private set; }

        public bool IsMulti
        {
            get => _isMulti;
            set
            {
                FunnelCheck.Write(this, FunnelField.IsMulti);
                _isMulti = value;

                if (!value)
                {
                    MultiDistanceBonus = 0;
                    MultiInfo = null;
                }
            }
        }

        public int MultiDistanceBonus { get; private set; }

        public bool IsCorpse => /*MathHelper.InRange(Graphic, 0x0ECA, 0x0ED2) ||*/
            Graphic == 0x2006;

        public bool OnGround => !SerialHelper.IsValid(Container);

        public uint RootContainer
        {
            get
            {
                Item item = this;
                int depth = 0;

                while (SerialHelper.IsItem(item.Container))
                {
                    item = World.FindItem(item.Container);

                    // T9 child-list slice B2: a chain longer than any nesting (a loop: WorldWriter refuses one) has no root
                    if (item == null || ++depth > WorldState.MaxNestingDepth)
                    {
                        return 0;
                    }
                }

                return SerialHelper.IsMobile(item.Container) ? item.Container : item;
            }
        }

        public ref StaticTiles ItemData =>
            ref Client.Game.UO.FileManager.TileData.StaticData[IsMulti ? MultiGraphic : Graphic];

        public bool IsLootable =>
            ItemData.Layer != (int)Layer.Hair
            && ItemData.Layer != (int)Layer.Beard
            && ItemData.Layer != (int)Layer.Face
            && Graphic != 0;

        private ushort _amount;
        private uint _container = 0xFFFF_FFFF;
        private bool _isDamageable;
        private Layer _layer;
        private byte _lightID;
        public ushort Amount { get { ScalarReads.Count(ScalarField.Amount); return _amount; } set { FunnelCheck.Write(this, FunnelField.Amount); _amount = value; } }
        public uint Container { get { ScalarReads.Count(ScalarField.Container); return _container; } set { FunnelCheck.Write(this, FunnelField.Container); _container = value; } }

        // ecs_small_fields: the record's ItemTraits with the switch on (SmallFields; CUO_SMALL_FIELDS_CHECK compares)
        public bool IsDamageable
        {
            get => SmallFields.Routed(World) ? SmallFields.ItemBit(this, SmallField.IsDamageable, ClassicUO.Ecs.ItemTraitBits.Damageable, _isDamageable) : _isDamageable;
            set { FunnelCheck.Write(this, FunnelField.IsDamageable); _isDamageable = value; }
        }
        public Layer Layer { get { ScalarReads.Count(ScalarField.Layer); return _layer; } set { FunnelCheck.Write(this, FunnelField.Layer); _layer = value; } }
        public byte LightID
        {
            get => SmallFields.Routed(World) ? SmallFields.LightId(this) : _lightID;
            set { FunnelCheck.Write(this, FunnelField.LightID); _lightID = value; }
        }

        public Rectangle? MultiInfo;
        public bool Opened;

        private bool _usedLayer;
        public uint Price
        {
            get
            {
                ScalarReads.Count(ScalarField.Price);

                return TryScalarEntity(out Friflo.Engine.ECS.Entity e) ? EcsScalars.Price(e) : 0;
            }
        }
        public bool UsedLayer
        {
            get => SmallFields.Routed(World) ? SmallFields.ItemBit(this, SmallField.UsedLayer, ClassicUO.Ecs.ItemTraitBits.UsedLayer, _usedLayer) : _usedLayer;
            set { FunnelCheck.Write(this, FunnelField.UsedLayer); _usedLayer = value; }
        }

        // ecs_small_fields: the cache behind the getters (the checker's other side, and the seed of a record created later)
        internal byte CachedLightId => _lightID;

        internal ClassicUO.Ecs.ItemTraits CachedTraits => new ClassicUO.Ecs.ItemTraits
        {
            LightId = _lightID,
            Bits = (_isDamageable ? ClassicUO.Ecs.ItemTraitBits.Damageable : 0) | (_usedLayer ? ClassicUO.Ecs.ItemTraitBits.UsedLayer : 0)
        };
        public bool WantUpdateMulti = true;

        public static Item Create(World world, uint serial)
        {
            Item i = new Item(world); // _pool.GetOne();
            i.Serial = serial;

            return i;
        }

        // ground items arrive after their chunk was built: their art (the graphic the view draws, and its animation frames)
        // decodes on the first-sight workers from here. Corpses draw from the animation path; a multi's art is its components'
        protected override void RequestFirstSightArt()
        {
            if (OnGround && !IsCorpse && !IsMulti)
            {
                global::ClassicUO.Game.Map.FirstSightArt.RequestPlaced(DisplayedGraphic);
            }
        }

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            if (Opened)
            {
                UIManager.GetGump<ContainerGump>(Serial)?.Dispose();
                UIManager.GetGump<SpellbookGump>(Serial)?.Dispose();
                UIManager.GetGump<MapGump>(Serial)?.Dispose();

                if (IsCorpse)
                {
                    UIManager.GetGump<GridLootGump>(Serial)?.Dispose();
                }

                UIManager.GetGump<BulletinBoardGump>(Serial)?.Dispose();
                UIManager.GetGump<SplitMenuGump>(Serial)?.Dispose();

                Opened = false;
            }

            base.Destroy();

            //_pool.ReturnOne(this);
        }

        private unsafe void LoadMulti()
        {
            WantUpdateMulti = false;

            short minX = 0;
            short minY = 0;
            short maxX = 0;
            short maxY = 0;

            if (!World.HouseManager.TryGetHouse(Serial, out House house))
            {
                house = new House(World, Serial, 0, false);
                World.HouseManager.Add(Serial, house);
            }
            else
            {
                house.ClearComponents();
            }

            var movable = false;
            // unit tests (no running client): an empty multi
            var multis = Client.Game != null ? Client.Game.UO.FileManager.Multis.GetMultis(Graphic) : NoMultis;

            bool gargoyle = PlayerIsGargoyle(World);

            for (var i = 0; i < multis.Count; ++i)
            {
                var block = multis[i];

                if (block.X < minX)
                {
                    minX = block.X;
                }

                if (block.X > maxX)
                {
                    maxX = block.X;
                }

                if (block.Y < minY)
                {
                    minY = block.Y;
                }

                if (block.Y > maxY)
                {
                    maxY = block.Y;
                }

                if (block.IsVisible)
                {
                    Multi m = Multi.Create(World, block.ID, gargoyle);
                    m.MultiOffsetX = block.X;
                    m.MultiOffsetY = block.Y;
                    m.MultiOffsetZ = block.Z;
                    m.Hue = Hue;
                    m.AlphaHue = 255;
                    m.IsCustom = false;
                    m.State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_DONT_REMOVE;
                    m.IsMovable = ItemData.IsMultiMovable;

                    m.SetInWorldTile(
                        (ushort)(X + block.X),
                        (ushort)(Y + block.Y),
                        (sbyte)(Z + block.Z)
                    );

                    house.Components.Add(m);

                    if (m.ItemData.IsMultiMovable)
                    {
                        movable = true;
                    }
                }
                else if (i == 0)
                {
                    MultiGraphic = block.ID;
                }
            }

            MultiInfo = new Rectangle
            {
                X = minX,
                Y = minY,
                Width = maxX,
                Height = maxY
            };

            // hack to make baots movable.
            // Mast is not the main center in bigger boats, so if we got a movable multi --> makes all multi movable
            if (movable)
            {
                foreach (Multi m in house.Components)
                {
                    m.IsMovable = movable;
                }
            }

            MultiDistanceBonus = Math.Max(
                Math.Max(Math.Abs(minX), maxX),
                Math.Max(Math.Abs(minY), maxY)
            );

            house.Bounds = MultiInfo.Value;

            UIManager.GetGump<MiniMapGump>()?.RequestUpdateContents();

            if (World.HouseManager.EntityIntoHouse(Serial, World.Player))
            {
                Client.Game.GetScene<GameScene>()?.UpdateMaxDrawZ(true);
            }

            // T1: through the writer, which runs it after the enclosing operation's scope closes when a writer operation
            // reloads the multi (the boat's resets are operations; they do not nest), else now
            World.Writer.ClearBoatSteps(Serial);
        }

        private static readonly System.Collections.Generic.List<MultiInfo> NoMultis = new System.Collections.Generic.List<MultiInfo>(0);

        public override void CheckGraphicChange(byte animIndex = 0)
        {
            if (!IsMulti && IsCorpse)
            {
                Direction dir = Direction;
                CorpseState(ref dir, out bool usedLayer, out Layer layer);
                AnimIndex = 99;
                UsedLayer = usedLayer;

                if (usedLayer)
                {
                    Direction = dir;
                }

                Layer = layer;
            }

            RefreshGraphicState();
        }

        /// <summary>
        /// T1: what <see cref="CheckGraphicChange"/> makes of a corpse's direction (the running bit sets UsedLayer and is
        /// stripped; the remaining direction is the corpse's layer). Its animation index becomes 99. Pure: the funnel computes
        /// a corpse's values with it before it writes them.
        /// </summary>
        internal static void CorpseState(ref Direction direction, out bool usedLayer, out Layer layer)
        {
            usedLayer = (direction & Direction.Running) != 0;

            if (usedLayer)
            {
                direction &= (Direction)0x7F;
            }

            layer = (Layer)direction;
        }

        /// <summary>
        /// T1: the part of <see cref="CheckGraphicChange"/> that writes no ECS-owned value: whether the item is drawn, and a
        /// multi's (re)load. Reads Graphic, IsMulti, WantUpdateMulti, X and Y, so it runs after the funnel wrote them.
        /// </summary>
        internal void RefreshGraphicState()
        {
            if (!IsMulti)
            {
                AllowedToDraw = IsCorpse || CanBeDrawn(World, Graphic);
            }
            else if (WantUpdateMulti)
            {
                World.UoAssist.SignalAddMulti((ushort)(Graphic | 0x4000), X, Y);

                if (
                    MultiDistanceBonus == 0
                    || World.HouseManager.IsHouseInRange(this, World.ClientViewRange)
                )
                {
                    LoadMulti();
                    AllowedToDraw = IsMultiItemDrawn(Client.Game != null ? Client.Game.UO.FileManager.TileData.StaticData : TestStaticData ?? Array.Empty<StaticTiles>(), MultiGraphic);
                }
            }
        }

        /// <summary>Whether a multi item with this display graphic (its first component) is drawn: the pack's tiledata
        /// draw flag. A graphic outside the table is drawn, as before.</summary>
        internal static bool IsMultiItemDrawn(StaticTiles[] staticData, ushort multiGraphic) =>
            multiGraphic >= staticData.Length || Uo.Pack.TileDraw.MultiItem(staticData[multiGraphic].PackFlags);

        public override void Update()
        {
            if (IsDestroyed)
            {
                return;
            }

            base.Update();

            ProcessAnimation();
        }

        public override ushort GetGraphicForAnimation()
        {
            var graphic = Graphic;

            if (Layer == Layer.Mount)
            {
                // ethereal unicorn
                if (graphic == 0x3E9B || graphic == 0x3E9D)
                {
                    return 0x00C0;
                }

                // ethereal kirin
                if (graphic == 0x3E9C)
                {
                    return 0x00BF;
                }

                if (Mounts.TryGet(graphic, out var mountInfo))
                {
                    graphic = mountInfo.Graphic;
                }

                if (ItemData.AnimID != 0)
                {
                    graphic = ItemData.AnimID;
                }
            }
            else if (IsCorpse)
            {
                return Amount;
            }

            return graphic;
        }

        public override void UpdateTextCoordsV()
        {
            if (TextContainer == null)
            {
                return;
            }

            TextObject last = (TextObject)TextContainer.Items;

            while (last?.Next != null)
            {
                last = (TextObject)last.Next;
            }

            if (last == null)
            {
                return;
            }

            int offY = 0;

            if (OnGround)
            {
                Point p = RealScreenPosition;

                var bounds = Client.Game.UO.Arts.GetRealArtBounds(Graphic);
                p.Y -= bounds.Height >> 1;

                p.X += (int)Offset.X + 22;
                p.Y += (int)(Offset.Y - Offset.Z) + 22;

                p = Client.Game.Scene.Camera.WorldToScreen(p, true);

                for (; last != null; last = (TextObject)last.Previous)
                {
                    if (last.RenderedText != null && !last.RenderedText.IsDestroyed)
                    {
                        if (offY == 0 && last.Time < Time.Ticks)
                        {
                            continue;
                        }

                        last.OffsetY = offY;
                        offY += last.RenderedText.Height;

                        last.RealScreenPosition.X = p.X - (last.RenderedText.Width >> 1);
                        last.RealScreenPosition.Y = p.Y - offY;
                    }
                }

                FixTextCoordinatesInScreen();
            }
            else
            {
                for (; last != null; last = (TextObject)last.Previous)
                {
                    if (last.RenderedText != null && !last.RenderedText.IsDestroyed)
                    {
                        if (offY == 0 && last.Time < Time.Ticks)
                        {
                            continue;
                        }

                        last.OffsetY = offY;
                        offY += last.RenderedText.Height;

                        last.RealScreenPosition.X = last.X - (last.RenderedText.Width >> 1);
                        last.RealScreenPosition.Y = last.Y - offY;
                    }
                }
            }
        }

        /// <summary>T5b-items: the corpse timer fields as this Item holds them (a pure read; the record push copies it into the
        /// component, the checker compares it).</summary>
        internal ClassicUO.Ecs.AnimationTimer CorpseTimer => new ClassicUO.Ecs.AnimationTimer
        {
            Frame = AnimIndex,
            ExecuteAnimation = ExecuteAnimation,
            AnimDue = LastAnimationChangeTime,
            UsedLayer = UsedLayer
        };

        public override void ProcessAnimation(bool evalutate = false)
        {
            if (!IsCorpse)
            {
                return;
            }

            var dir = (byte)Layer;

            if (LastAnimationChangeTime < Time.Ticks)
            {
                byte frameIndex = (byte)(AnimIndex + (ExecuteAnimation ? 1 : 0));
                ushort id = GetGraphicForAnimation();

                bool mirror = false;

                var animations = Client.Game.UO.Animations;
                animations.GetAnimDirection(ref dir, ref mirror);

                // the corpse is drawn as its Corpse.def target: the death action and frame count come from that body
                // (AnimationsLoader.TryCorpseFrame); when the animation is done, stop at the last frame
                int next = -1;

                if (
                    id < animations.MaxAnimationCount
                    && dir < 5
                    && Client.Game.UO.FileManager.Animations.TryCorpseFrame(id, UsedLayer, dir, frameIndex, out byte frame)
                )
                {
                    next = frame;
                }

                // T1 (funnel part 8): the frame and the due time through the funnel
                World.Writer.AdvanceCorpseFrame(this, next, Time.Ticks + Constants.CHARACTER_ANIMATION_DELAY);
            }
        }

        public void GetTotalAmount(ushort graphic, ushort? hue, ref int amount) => GetTotalAmount(graphic, hue, ref amount, Serial, 0);

        // T9 child-list slice B2: bounded and not back to its root's serial, as Entity.FindItem
        private void GetTotalAmount(ushort graphic, ushort? hue, ref int amount, uint root, int depth)
        {
            // T2: the children from the ECS (WorldState.Children)
            foreach (Item item in World.State.Children(this))
            {
                if (item != null)
                {
                    if (depth != 0 && item.Serial == root)
                    {
                        continue;
                    }

                    if (depth < WorldState.MaxNestingDepth)
                    {
                        item.GetTotalAmount(graphic, hue, ref amount, root, depth + 1);
                    }

                    if (item.Graphic == graphic && (hue == null || item.Hue == hue.Value) && item.Exists)
                    {
                        amount += item.Amount;
                    }
                }
            }
        }
    }
}
