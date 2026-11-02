// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// What <see cref="ItemSpriteExtract"/> reads for one world item. <see cref="Gather"/> fills it from the item's mirror record
    /// (through the cached handle) and, for what has no ECS state yet, from the GameObject and the scene.
    /// </summary>
    internal struct ItemSpriteInput
    {
        // the mirror record (ECS): Serial, Graphic, Hue, Position, Flags, ItemData (amount, layer; only when UsesItemData),
        // the OnGround and Multi tags
        public uint Serial;
        public ushort Graphic, Hue, Amount;
        public ushort X;
        public byte Layer;
        public bool OnGround, Multi, Hidden, Movable;

        // tiledata of the item's graphic (the multi's graphic for a multi)
        public bool TileLight, TilePartialHue, TileAnimated, TileContainer, TileStackable, TileHeavy;

        // still only on the GameObject (T7b part 2): drawability, the render list's alpha, the render offset, the multi's
        // graphic, the corpse animation (frame, second death, the mirror history) and the destroyed state
        public bool Destroyed, AllowedToDraw, UsedLayer, Flipped;
        public byte AlphaHue, AnimIndex;
        public Vector3 Offset;
        public ushort MultiGraphic, AnimGraphic;

        // the scene: selection, profile options, targeting, the player
        public bool Selected, SelectedContainer, HighlightSelected, OutOfRange, PlayerDeadGray, ReplaceFields, PlacementPreview;
        public bool CorpseHidden, CorpseLootSelected;

        /// <summary>A corpse's GameObject, for its worn children (FindItemByLayer, LayerRuleEval.CorpseOrder); null otherwise.</summary>
        public Entity CorpseSource;

        public readonly bool IsCorpse => Graphic == 0x2006;

        public readonly bool IsCoin => Graphic == 0x0EEA || Graphic == 0x0EED || Graphic == 0x0EF0;

        public readonly bool Locked => !Movable && TileHeavy;

        /// <summary>Whether the select reads <see cref="Amount"/> or <see cref="Layer"/>: the coin graphic, the stack's second
        /// copy, the corpse (body, direction, human check). <see cref="ReadRecord"/> fills them only then.</summary>
        public readonly bool UsesItemData => IsCoin || IsCorpse || !Multi && TileStackable;

        /// <summary>Item.DisplayedGraphic.</summary>
        public readonly ushort DisplayedGraphic
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
                else if (Multi)
                {
                    return MultiGraphic;
                }

                return Graphic;
            }
        }

        /// <summary>The input for <paramref name="item"/>: its mirror record when it has one (every server item; not the
        /// house placement preview), else the GameObject's cached fields.</summary>
        public static void Gather(Item item, out ItemSpriteInput i)
        {
            i = default;
            i.Destroyed = item.IsDestroyed;

            if (i.Destroyed)
            {
                return;
            }

            World world = item.World;
            ReadRecord(item, Client.Game.UO.FileManager.TileData.StaticData, ref i, out ushort y);

            i.AllowedToDraw = item.AllowedToDraw;

            if (!i.AllowedToDraw)
            {
                return;
            }

            i.AlphaHue = item.AlphaHue;
            i.Offset = item.Offset;
            i.AnimIndex = item.AnimIndex;
            i.UsedLayer = item.UsedLayer;
            i.Flipped = item.ViewFlipped;

            // GameObject.Distance for an item
            int distance = world.Player == null ? ushort.MaxValue : Math.Max(Math.Abs(i.X - world.RangeSize.X), Math.Abs(y - world.RangeSize.Y));
            Profile profile = ProfileManager.CurrentProfile;
            i.Selected = ReferenceEquals(SelectedObject.Object, item);
            i.HighlightSelected = profile.HighlightGameObjects && i.Selected;
            i.OutOfRange = profile.NoColorObjectsOutOfRange && distance > world.ClientViewRange;
            i.PlayerDeadGray = world.Player.IsDead && profile.EnableBlackWhiteEffect;

            if (i.IsCorpse)
            {
                i.CorpseHidden = world.CorpseManager.Exists(i.Serial, 0);
                i.CorpseLootSelected = profile.GridLootType > 0 && SelectedObject.CorpseObject == item;
                i.AnimGraphic = item.GetGraphicForAnimation();
                i.CorpseSource = item;

                return;
            }

            i.ReplaceFields = i.TileAnimated && profile.FieldsType == 2;
            i.SelectedContainer = i.TileContainer && SelectedObject.SelectedContainer == item;
            i.PlacementPreview = !SerialHelper.IsValid(i.Serial) && i.Multi && world.TargetManager.TargetingState == CursorTarget.MultiPlacement;
        }

        /// <summary>The record part of <see cref="Gather"/>: the mirror record (or the GameObject without one) and the tiledata
        /// row. Split out so the per-item cost can be measured without a running client (GatherCostBench).</summary>
        /// <remarks>
        /// Per drawn item per frame, so it reads the record once (<c>Entity.Data</c>: one node lookup for every component) and
        /// skips what the select does not use: the Serial component is the item's serial (TryScalarEntity found the record
        /// under it), and ItemData (Amount, Layer) is read only for coins, corpses and stackable art, the only items whose
        /// sprites depend on it; Amount and Layer stay 0 for the others. GatherCostBench measures it against the field reads.
        /// </remarks>
        internal static void ReadRecord(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            bool record = item.TryScalarEntity(out EcsEntity e);
            Friflo.Engine.ECS.EntityData data = default;

            if (record)
            {
                data = e.Data;
                i.Serial = item.Serial;
                i.Graphic = data.Get<ClassicUO.Ecs.Graphic>().Value;
                i.Hue = data.Get<ClassicUO.Ecs.Hue>().Value;
                ref readonly ClassicUO.Ecs.Position p = ref data.Get<ClassicUO.Ecs.Position>();
                i.X = p.X;
                y = p.Y;
                var flags = (Flags)data.Get<ClassicUO.Ecs.Flags>().Value;
                i.Hidden = (flags & Flags.Hidden) != 0;
                i.Movable = (flags & Flags.Movable) != 0;
                Friflo.Engine.ECS.Tags tags = data.Tags;
                i.OnGround = tags.Has<ClassicUO.Ecs.OnGround>();
                i.Multi = tags.Has<ClassicUO.Ecs.MultiTag>();
            }
            else
            {
                i.Serial = item.Serial;
                i.Graphic = item.Graphic;
                i.Hue = item.Hue;
                i.X = item.X;
                y = item.Y;
                i.Hidden = item.IsHidden;
                i.Movable = (item.Flags & Flags.Movable) != 0;
                i.Amount = item.Amount;
                i.Layer = (byte)item.Layer;
                i.OnGround = item.OnGround;
                i.Multi = item.IsMulti;
            }

            i.MultiGraphic = item.MultiGraphic;
            ref StaticTiles tile = ref staticData[i.Multi ? i.MultiGraphic : i.Graphic];
            i.TileLight = tile.IsLight;
            i.TilePartialHue = tile.IsPartialHue;
            i.TileAnimated = tile.IsAnimated;
            i.TileContainer = tile.IsContainer;
            i.TileStackable = tile.IsStackable;
            i.TileHeavy = tile.Weight > 90;

            if (record && i.UsesItemData)
            {
                ref readonly ClassicUO.Ecs.ItemData d = ref data.Get<ClassicUO.Ecs.ItemData>();
                i.Amount = d.Amount;
                i.Layer = d.Layer;
            }
        }
    }

    /// <summary>
    /// W3 for world items (ItemView.Draw): the <see cref="SpriteInstance"/>s the Gpu world's view capture produces —
    /// for art items and multis the stack's second copy, the animated-art frame, the per-frame hue rules (selection,
    /// container highlight, field replacement, out of range, dead gray, hidden, the multi placement preview) from the pack's
    /// art table; for corpses the body and the worn layers in paint order from the pack's animation tables (DrawCorpse) —
    /// with no atlas and no batcher. Side effects of the view kept: the light request and the art entry's draw offset that
    /// picking reads (ArtEntry.Width/Height), and a corpse's mirror bit (GetAnimDirection history, <see cref="Flipped"/>; the
    /// caller stores it, corpse picking reads it).
    /// T7b: split into select (<see cref="DynamicSprite"/> records, renderer-neutral, from <see cref="ItemSpriteInput"/> and
    /// the pack-backed loaders) and the Gpu resolve (<see cref="GpuSpriteResolve"/>); <see cref="Emit"/> does both.
    /// </summary>
    internal sealed class ItemSpriteExtract
    {
        private const int RANK_ITEM = 6;

        private readonly ArtLoader _artsOverride;
        private readonly AnimationsLoader _animsOverride;
        private SpriteInstance[] _out = new SpriteInstance[8];
        private int _count;
        private DynamicSprite[] _sel = new DynamicSprite[8];
        private int _selCount;
        private bool _selectOnly;
        private GpuSpriteTables _tables;
        private int _offsetX, _offsetY;

        public ItemSpriteExtract() { }

        /// <summary>Tests: the loaders the select reads instead of the client's.</summary>
        internal ItemSpriteExtract(ArtLoader arts, AnimationsLoader anims)
        {
            _artsOverride = arts;
            _animsOverride = anims;
        }

        private ArtLoader Arts => _artsOverride ?? Client.Game.UO.FileManager.Arts;

        public ReadOnlySpan<SpriteInstance> Instances => new ReadOnlySpan<SpriteInstance>(_out, 0, _count);

        /// <summary>The last <see cref="Select"/>'s records.</summary>
        public ReadOnlySpan<DynamicSprite> Selected => new ReadOnlySpan<DynamicSprite>(_sel, 0, _selCount);

        /// <summary>The view would add a light for this item at (LightX, LightY) (GameScene.AddLight(item, item, …)).</summary>
        public bool Light { get; private set; }

        public int LightX { get; private set; }

        public int LightY { get; private set; }

        /// <summary>A corpse's mirror bit after GetAnimDirection; the caller stores it in Item.ViewFlipped when
        /// <see cref="SetsFlipped"/>.</summary>
        public bool Flipped { get; private set; }

        public bool SetsFlipped { get; private set; }

        /// <summary>True when the extract reproduces this item's view: every world item (art items, multis and corpses).</summary>
        public static bool Handles(Item item) => true;

        /// <summary>ItemView.Draw for one item at screen position (<paramref name="posX"/>, <paramref name="posY"/>): select, then
        /// the Gpu resolve into <see cref="Instances"/>. False = not drawn.</summary>
        public bool Emit(in ItemSpriteInput item, PackSet pack, int offsetX, int offsetY, int posX, int posY, float depth)
        {
            _count = 0;

            if (pack != null)
            {
                _tables = new GpuSpriteTables(pack);
            }

            return Run(in item, pack != null && pack.HasAnims, offsetX, offsetY, posX, posY, depth, false);
        }

        /// <summary><see cref="Emit(in ItemSpriteInput, PackSet, int, int, int, int, float)"/> over explicit tables (tests).</summary>
        internal bool Emit(in ItemSpriteInput item, in GpuSpriteTables tables, int offsetX, int offsetY, int posX, int posY, float depth)
        {
            _count = 0;
            _tables = tables;

            return Run(in item, tables.AnimFrames != null, offsetX, offsetY, posX, posY, depth, false);
        }

        /// <summary>T7b select: the records of <see cref="Emit"/> into <see cref="Selected"/> (with the same light, mirror bit
        /// and picking offset side effects), no instances. <paramref name="hasAnims"/>: the pack has animations (corpses).</summary>
        public bool Select(in ItemSpriteInput item, bool hasAnims, int posX, int posY, float depth)
        {
            _selCount = 0;

            return Run(in item, hasAnims, 0, 0, posX, posY, depth, true);
        }

        private bool Run(in ItemSpriteInput item, bool hasAnims, int offsetX, int offsetY, int posX, int posY, float depth, bool selectOnly)
        {
            _selectOnly = selectOnly;
            _offsetX = offsetX;
            _offsetY = offsetY;
            Light = false;
            SetsFlipped = false;

            if (item.Destroyed)
            {
                return false;
            }

            ushort displayed = item.DisplayedGraphic;

            if (item.TileLight || displayed >= 0x3E02 && displayed <= 0x3E0B || displayed >= 0x3914 && displayed <= 0x3929)
            {
                Light = true;
                LightX = posX + 22;
                LightY = posY + 22;
            }

            if (!item.AllowedToDraw)
            {
                return false;
            }

            posX += (int)item.Offset.X;
            posY += (int)(item.Offset.Y + item.Offset.Z);
            float alpha = item.AlphaHue / 255f;

            if (item.IsCorpse)
            {
                return EmitCorpse(in item, hasAnims, posX, posY - 3, ShaderHueTranslator.GetHueVector(0, false, alpha).Z, depth);
            }

            var hueInput = new HueInput
            {
                Hue = item.Hue,
                Graphic = displayed,
                RawGraphic = item.Graphic,
                Partial = item.TilePartialHue,
                OnGround = item.OnGround,
                ReplaceFields = item.ReplaceFields,
                SelectedContainer = item.SelectedContainer,
                HighlightSelected = item.HighlightSelected,
                OutOfRange = item.OutOfRange,
                PlayerDeadGray = item.PlayerDeadGray,
                Selected = item.Selected,
                Locked = item.Locked,
                Multi = item.Multi,
                Hidden = item.Hidden
            };

            ChooseHue(in hueInput, out ushort graphic, out ushort hue, out bool partial);

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, alpha);
            int objX = item.X;

            if (!item.Multi && !item.IsCoin && item.Amount > 1 && item.TileStackable)
            {
                Art(graphic, posX - 5, posY - 5, hueVec, depth, objX);
            }

            // the house placement preview (a local multi item without a serial) is drawn half transparent
            if (item.PlacementPreview)
            {
                hueVec.Z = 0.5f;
            }

            Art(graphic, posX, posY, hueVec, depth, objX);

            return true;
        }

        private void Add(in DynamicSprite sprite)
        {
            if (_selectOnly)
            {
                if (_selCount >= _sel.Length)
                {
                    Array.Resize(ref _sel, _sel.Length * 2);
                }

                _sel[_selCount++] = sprite;

                return;
            }

            GpuSpriteResolve.Resolve(in _tables, in sprite, _offsetX, _offsetY, ref _out, ref _count);
        }

        /// <summary>What ItemView.Draw's art hue choice reads from the item, the profile and the selection.</summary>
        internal struct HueInput
        {
            public ushort Hue;
            public ushort Graphic;       // DisplayedGraphic
            public ushort RawGraphic;    // Item.Graphic, which the field tests use
            public bool Partial;
            public bool OnGround;
            public bool ReplaceFields;   // animated art and the Fields option is 2
            public bool SelectedContainer;
            public bool HighlightSelected;
            public bool OutOfRange;
            public bool PlayerDeadGray;
            public bool Selected;
            public bool Locked;
            public bool Multi;
            public bool Hidden;
        }

        /// <summary>ItemView.Draw's graphic, hue and partial-hue choice: field replacement and the selected container (ground
        /// items), then the first that applies of highlight, out of range, player dead gray, mouse selection and hidden.</summary>
        internal static void ChooseHue(in HueInput i, out ushort graphic, out ushort hue, out bool partial)
        {
            hue = i.Hue;
            graphic = i.Graphic;
            partial = i.Partial;

            if (i.OnGround)
            {
                if (i.ReplaceFields)
                {
                    ushort g = i.RawGraphic;

                    if (StaticFilters.IsFireField(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x0020;
                    }
                    else if (StaticFilters.IsParalyzeField(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x0058;
                    }
                    else if (StaticFilters.IsEnergyField(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x0070;
                    }
                    else if (StaticFilters.IsPoisonField(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x0044;
                    }
                    else if (StaticFilters.IsWallOfStone(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x038A;
                    }
                }

                if (i.SelectedContainer)
                {
                    hue = 0x0035;
                    partial = false;
                }
            }

            if (i.HighlightSelected)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (i.OutOfRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (i.PlayerDeadGray)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }
            else if (!i.Locked && !i.Multi && i.Selected)
            {
                hue = 0x0035;
            }
            else if (i.Hidden)
            {
                hue = 0x038E;
            }
        }

        // ItemView.DrawCorpse: the body, then the worn layers in paint order (PaperdollOrder.BuildInWorld)
        private bool EmitCorpse(in ItemSpriteInput item, bool hasAnims, int posX, int posY, float alpha, float depth)
        {
            if (item.CorpseHidden || !hasAnims)
            {
                return false;
            }

            posX += 22;
            posY += 22;

            byte direction = (byte)(item.Layer & 0x7F & 7);
            bool flipped = item.Flipped;
            var animations = Client.Game.UO.Animations;
            animations.GetAnimDirection(ref direction, ref flipped);
            Flipped = flipped;
            SetsFlipped = true;

            ushort graphic = item.AnimGraphic;
            ushort converted = graphic;
            animations.ConvertBodyIfNeeded(ref converted, isCorpse: true);
            AnimationsLoader anims = _animsOverride ?? Client.Game.UO.FileManager.Animations;
            byte group = anims.GetDeathAction(converted, animations.GetAnimFlags(converted), animations.GetAnimType(converted), item.UsedLayer);
            bool human = IsHumanCorpse(item.Amount);

            CorpseLayer(in item, anims, Layer.Invalid, human, item.Hue, flipped, alpha, group, direction, posX, posY, depth);

            Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];
            int layerCount = LayerRuleEval.CorpseOrder(item.CorpseSource, item.Amount, direction, layers); // T3b: from data with ecs_layer_rules

            for (int i = 0; i < layerCount; i++)
            {
                CorpseLayer(in item, anims, layers[i], human, 0, flipped, alpha, group, direction, posX, posY, depth);
            }

            return true;
        }

        private static bool IsHumanCorpse(ushort body) =>
            body >= 0x0190 && body <= 0x0193 || body >= 0x00B7 && body <= 0x00BA || body >= 0x025D && body <= 0x0260 ||
            body >= 0x029A && body <= 0x029B || body >= 0x02B6 && body <= 0x02B7 ||
            body == 0x03DB || body == 0x03DF || body == 0x03E2 || body == 0x02E8 || body == 0x02E9;

        // ItemView.DrawLayer: one layer's frame at target pixel (x, y) (screen position + draw offset), in strips
        private void CorpseLayer(in ItemSpriteInput owner, AnimationsLoader anims, Layer layer, bool human, ushort color,
            bool flipped, float alpha, byte group, byte dir, int x, int y, float depth)
        {
            bool partial = false;
            bool hasConv = false;
            ushort convColor = 0;
            ushort graphic;
            byte animIndex = owner.AnimIndex;

            if (layer == Layer.Invalid)
            {
                graphic = owner.AnimGraphic;
            }
            else if (human)
            {
                Item equip = owner.CorpseSource.FindItemByLayer(layer);

                if (equip == null)
                {
                    return;
                }

                graphic = equip.ItemData.AnimID;
                partial = equip.ItemData.IsPartialHue;

                // as the view: the conversion table is looked up by the item's animation id twice
                if (anims.EquipConversions.TryGetValue(graphic, out var map) && map.TryGetValue(graphic, out EquipConvData data))
                {
                    hasConv = true;
                    convColor = data.Color;
                    graphic = data.Graphic;
                }

                color = equip.Hue;
            }
            else
            {
                return;
            }

            bool isEquip = layer != Layer.Invalid;

            if (!MobileSpriteExtract.TryGetFrames(anims, graphic, group, dir, isEquip, !isEquip, out uint first, out int count, out ushort fileHue))
            {
                return;
            }

            if (color == 0)
            {
                color = fileHue;
            }

            if (animIndex >= count)
            {
                animIndex = (byte)(count - 1);
            }

            uint index = first + animIndex;
            ref readonly AnimFrameEntry fe = ref anims.PackFrames[index];
            int w = fe.Sprite.OrigW, h = fe.Sprite.OrigH;

            // no texture
            if (w == 0 || h == 0)
            {
                return;
            }

            x -= flipped ? w - fe.CenterX : fe.CenterX;
            y -= h + fe.CenterY;

            if (color == 0 && hasConv)
            {
                color = convColor;
                partial = false;
            }

            Vector3 hueVec;

            if (owner.OutOfRange)
            {
                hueVec = ShaderHueTranslator.GetHueVector(Constants.OUT_RANGE_COLOR + 1, false, 1);
            }
            else if (owner.PlayerDeadGray)
            {
                hueVec = ShaderHueTranslator.GetHueVector(Constants.DEAD_RANGE_COLOR + 1, false, 1);
            }
            else
            {
                if (owner.CorpseLootSelected)
                {
                    color = 0x0034;
                }
                else if (owner.HighlightSelected)
                {
                    color = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                }

                hueVec = ShaderHueTranslator.GetHueVector(color, partial, alpha);
            }

            // the view's strips: rows [0, value) and [value, h) with value = max(1, h + centre y); later strips are empty
            int value = Math.Max(1, h + fe.CenterY);
            int strips = Math.Max(h / value + 1, 2);
            int srcY = 0, srcH = Math.Min(value, h), remains = h - srcH;
            var sprite = new DynamicSprite
            {
                Kind = DynamicSpriteKind.Animation,
                Flags = (flipped ? DynamicSpriteFlags.Flip : 0) | (isEquip ? DynamicSpriteFlags.Equip : DynamicSpriteFlags.Corpse),
                Rank = RANK_ITEM,
                Body = graphic,
                Group = group,
                Dir = dir,
                Frame = animIndex,
                Id = index,
                X = x,
                Hue = hueVec,
                ObjX = owner.X
            };

            for (int i = 0; i < strips; i++)
            {
                sprite.Y = y + srcY;
                sprite.SrcY = srcY;
                sprite.SrcH = srcH;
                sprite.Depth = depth + 1f + i * 2;
                Add(in sprite);
                srcY += srcH;
                srcH = remains;
                remains -= srcH;
            }
        }

        // View.DrawStaticAnimated (no shadow, not wet): the art graphic's record at (x, y) less the art entry's draw offset
        private void Art(ushort graphic, int x, int y, Vector3 hue, float depth, int objX)
        {
            ArtLoader arts = Arts;
            graphic = (ushort)(graphic + arts.GetEntry(graphic + 0x4000).AnimOffset);
            uint idx = (uint)graphic + 0x4000;
            SpriteEntry[] sprites = arts.PackSpriteEntries;

            if (idx >= sprites.Length)
            {
                return;
            }

            ref readonly SpriteEntry sp = ref sprites[idx];
            int ow = sp.OrigW, oh = sp.OrigH;

            if (ow == 0 || oh == 0)
            {
                // Legacy: the view draws Arts.GetArt's placeholder for art the pack lacks, offset by the placeholder's size
                // (the resolve does that). Its art entry write is discarded (GetEntry of missing art is a scratch entry), so
                // picking keeps 0, 0 and nothing is stored here. The Gpu path draws nothing.
                if (_selectOnly)
                {
                    var missing = new DynamicSprite
                    {
                        Kind = DynamicSpriteKind.Art,
                        Flags = DynamicSpriteFlags.MissingArt,
                        Rank = RANK_ITEM,
                        Id = graphic,
                        X = x,
                        Y = y,
                        Hue = hue,
                        Depth = depth + 0.5f,
                        ObjX = objX
                    };

                    Add(in missing);
                }

                return;
            }

            // the draw offset the view stores on the art entry; item picking reads it
            ref ArtEntry index = ref arts.GetEntry(graphic + 0x4000);
            index.Width = (short)((ow >> 1) - 22);
            index.Height = (short)(oh - 44);

            var sprite = new DynamicSprite
            {
                Kind = DynamicSpriteKind.Art,
                Rank = RANK_ITEM,
                Id = graphic,
                X = x - index.Width,
                Y = y - index.Height,
                SrcH = oh,
                Hue = hue,
                Depth = depth + 0.5f,
                ObjX = objX
            };

            Add(in sprite);
        }
    }
}
