// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// Turns a mobile's ECS animation components into Uo.Render <see cref="SpriteInstance"/>s straight from the pack's
    /// animation tables — the same instances GpuWorldRenderer's view capture produces for MobileView.Draw (shadows, mount,
    /// body, equipment in paint order, strips, trim, mirroring, hue mode, depth key), with no atlas and no batcher.
    /// Screen position and base depth are inputs; the Gpu world renderer and the Legacy sink draw from it.
    /// </summary>
    /// <summary>asset_prefetch_on_packet: whether a dynamic sprite instance's page is resident in the Gpu world, and its
    /// request (queued like a prefetch, not counted as a missing visible page).</summary>
    internal interface IDynamicPageResidency
    {
        bool Resident(in SpriteInstance instance);

        void Request(in SpriteInstance instance);
    }

    internal sealed class MobileSpriteExtract
    {
        private const int RANK_MOBILE = 7;
        private const int MAX_STRIPS = 512;

        private readonly AnimationsLoader _anims;
        private PackSet _pack;
        private SpriteInstance[] _out = new SpriteInstance[256];
        private int _count;
        private int _offsetX, _offsetY;
        private int _objX;
        private int _fiX, _fiY, _fiW, _fiH;
        private LightRequest[] _lights = new LightRequest[8];
        private int _lightCount;

        /// <summary>A light MobileView.Draw would add (GameScene.AddLight(mobile, item, X, Y)), in screen pixels.</summary>
        public struct LightRequest
        {
            public uint Item;
            public int X, Y;
        }

        public MobileSpriteExtract(AnimationsLoader anims)
        {
            _anims = anims;
        }

        // E5: after a cached replay the instances are the cache's own absolute copy (MobileFrameCache.Abs), exposed as is
        // instead of being copied into _out first; the consumer copies them once, into the dynamic buffer. Only while
        // nothing else was emitted since Begin (_count == 0); anything emitted after it first moves the view into _out.
        private SpriteInstance[] _viewArr;
        private int _viewCount;

        public ReadOnlySpan<SpriteInstance> Instances => _viewArr != null ? new ReadOnlySpan<SpriteInstance>(_viewArr, 0, _viewCount) : new ReadOnlySpan<SpriteInstance>(_out, 0, _count);

        public int Count => _viewArr != null ? _viewCount : _count;

        private void MaterializeView()
        {
            if (_viewArr == null)
            {
                return;
            }

            if (_count + _viewCount > _out.Length)
            {
                Array.Resize(ref _out, Math.Max(_out.Length * 2, _count + _viewCount));
            }

            Array.Copy(_viewArr, 0, _out, _count, _viewCount);
            _count += _viewCount;
            _viewArr = null;
            _viewCount = 0;
        }

        /// <summary>The last <see cref="Emit"/>'s MobileView FrameInfo (picking, text and health-bar anchoring).</summary>
        public Rectangle FrameInfo => new Rectangle(_fiX, _fiY, _fiW, _fiH);

        /// <summary>The lights the last <see cref="Emit"/> asks for, in the view's order.</summary>
        public ReadOnlySpan<LightRequest> Lights => new ReadOnlySpan<LightRequest>(_lights, 0, _lightCount);

        public void Begin(PackSet pack, int offsetX, int offsetY)
        {
            _pack = pack;
            _hasAnims = pack != null && pack.HasAnims;

            if (_hasAnims)
            {
                _tables = new GpuSpriteTables(pack);
            }

            BeginCore(offsetX, offsetY);
        }

        /// <summary><see cref="Begin(PackSet, int, int)"/> over explicit tables (tests: no pack on disk).</summary>
        internal void Begin(in GpuSpriteTables tables, int offsetX, int offsetY)
        {
            _pack = null;
            _hasAnims = tables.AnimFrames != null;
            _tables = tables;
            BeginCore(offsetX, offsetY);
        }

        /// <summary>T7b Legacy sink: <see cref="Select"/> only. The select reads frame sizes from the loader's pack tables, so
        /// no PackSet is needed; nothing is resolved to instances. <paramref name="atlas"/>: a layer whose atlas frame has no
        /// texture yet (a direction decoding on a worker) adds no records, FrameInfo or light, as in MobileView.DrawInternal.</summary>
        internal void BeginSelect(ILegacySpriteSource atlas = null)
        {
            _pack = null;
            _hasAnims = _anims.PackFrames != null;
            _tables = default;
            BeginCore(0, 0);
            _atlas = atlas;
        }

        private void BeginCore(int offsetX, int offsetY)
        {
            _atlas = null;
            _geo = Scenes.DepthModel.On;
            _submerged = false;
            _wall = default;
            _offsetX = offsetX;
            _offsetY = offsetY;
            _count = 0;
            _viewArr = null;
            _lightCount = 0;
        }

        public void Clear()
        {
            _count = 0;
            _viewArr = null;
            _lightCount = 0;
        }

        /// <summary>
        /// T7b select: the renderer-neutral records of one mobile (shadows, mount, body, equipment in paint order, strips and
        /// seated sections) into <see cref="Selected"/>, plus the same FrameInfo and lights as <see cref="Emit(EcsEntity, int, int, float)"/>;
        /// no instances. <see cref="GpuSpriteResolve"/> of each record in order gives Emit's instances.
        /// </summary>
        public void Select(EcsEntity e, int posX, int posY, float depth) => Select(e, EcsRead.Row(e), posX, posY, depth);

        /// <summary><see cref="Select(EcsEntity, int, int, float)"/> with <paramref name="e"/>'s record already read (perf-11).</summary>
        public void Select(EcsEntity e, in EcsRow data, int posX, int posY, float depth)
        {
            _selCount = 0;
            _selectOnly = true;
            _atlasMiss = false;

            try
            {
                Emit(e, data, posX, posY, depth);
            }
            finally
            {
                _selectOnly = false;
            }
        }

        /// <summary>The last <see cref="Select"/>'s records.</summary>
        public ReadOnlySpan<DynamicSprite> Selected => new ReadOnlySpan<DynamicSprite>(_sel, 0, _selCount);

        /// <summary>The tables and draw offset of the last Begin (for resolving <see cref="Selected"/>).</summary>
        internal ref readonly GpuSpriteTables Tables => ref _tables;

        internal int OffsetX => _offsetX;

        internal int OffsetY => _offsetY;

        private DynamicSprite[] _sel = new DynamicSprite[64];

        // the depth recipe of each selected record (MobileFrameCache's Legacy replay applies it to the frame's base depth)
        private byte[] _selKind = new byte[64];
        private float[] _selAdd = new float[64];
        private int _selCount;

        // the last Select skipped a layer whose atlas frame had no texture yet: its result must not be replayed later
        private bool _atlasMiss;
        private bool _selectOnly;
        private bool _hasAnims;
        private GpuSpriteTables _tables;
        private ILegacySpriteSource _atlas;

        /// <summary>Appends the instances of one mobile drawn at screen position (<paramref name="posX"/>, <paramref name="posY"/>) with the scene's base depth.</summary>
        public void Emit(EcsEntity e, int posX, int posY, float depth) => Emit(e, EcsRead.Row(e), posX, posY, depth);

        // perf-11: one record read (the entity's EcsRow) for all the components the emission reads
        /// <summary>clip-4: the composite anchor <see cref="Emit(EcsEntity, in EcsRow, int, int, float)"/> gives every layer's
        /// record (DynamicSprite.GeoAx/GeoAy) for a mobile drawn at (<paramref name="posX"/>, <paramref name="posY"/>): MobileView's
        /// draw point with the step offset, moved by the seat offset when sitting; the mount's seat offset does not move it.</summary>
        internal static void GeoAnchor(in AnimationState s, in MobileRenderState rs, int posX, int posY, out int ax, out int ay)
        {
            ax = posX + rs.OffsetX + 22;
            ay = posY - 3 + rs.OffsetY + 22;

            if ((s.Flags & AnimStateFlags.MountDrawn) == 0 && (s.Flags & AnimStateFlags.Sitting) != 0)
            {
                ax += s.SitX;
                ay += s.SitY;
            }
        }

        private void Emit(EcsEntity e, in EcsRow data, int posX, int posY, float depth)
        {
            MaterializeView();
            ref readonly AnimationState s = ref data.Get<AnimationState>();

            if ((s.Flags & AnimStateFlags.Visible) == 0 || !_hasAnims)
            {
                return;
            }

            ref readonly MobileRenderState rs = ref data.Get<MobileRenderState>();
            ref readonly EquipmentSlots eq = ref data.Get<EquipmentSlots>();
            _objX = data.Get<Position>().X;
            _objZ = data.Get<Position>().Z;
#if CUO_DEVTOOLS
            _objY = data.Get<Position>().Y;
            _objStill = rs.OffsetX == 0 && rs.OffsetY == 0;
#endif
            _fiX = _fiY = _fiW = _fiH = 0;
            ushort bodyHue = data.Get<Hue>().Value;

            int drawX = posX + rs.OffsetX + 22;
            int drawY = posY - 3 + rs.OffsetY + 22;
            // clip-2: one composite anchor for every layer, taken before the mount's seat offset moves the rider and its
            // equipment, so mount, rider and equipment get the same geometric depth (depth_model)
            _geoAnchorX = drawX;
            _geoAnchorY = drawY;
            // mobile_wall_order: the centre column of the tile the depth key sorts the mobile on
            _wallRefX = posX + 22 + _wall.Du * 22;
            _wallRefY = posY + 22 + _wall.Dv * 22;
            float alpha = rs.Alpha / 255f;
            bool mirror = (s.Flags & AnimStateFlags.Mirror) != 0;
            bool shadow = (s.Flags & AnimStateFlags.Shadow) != 0;
            bool charSitting = (s.Flags & AnimStateFlags.CharSitting) != 0;
            _human = (s.Flags & AnimStateFlags.Human) != 0;
            byte frame = s.Frame;
            int mountOffset = s.MountOffsetY;
            ushort over = rs.OverrideHue;

            if ((s.Flags & AnimStateFlags.MountDrawn) != 0)
            {
                if (shadow)
                {
                    Layer(s.Body, s.Group, s.Dir, frame, false, true, drawX, drawY + 10, mirror, alpha, depth, mountOffset, over, 0, false, false, false, 0, charSitting, 0);
                    Layer(s.MountBody, s.MountGroup, s.Dir, frame, false, true, drawX, drawY, mirror, alpha, depth, mountOffset, over, 0, false, false, false, 0, charSitting, 0);
                }

                Layer(s.MountBody, s.MountGroup, s.Dir, frame, false, false, drawX, drawY, mirror, alpha, depth, mountOffset, over, eq.MountHue, true, true, false, 0, charSitting, 0);
                drawY += mountOffset;
            }
            else if ((s.Flags & AnimStateFlags.Sitting) != 0)
            {
                drawX += s.SitX;
                drawY += s.SitY;
                _geoAnchorX = drawX;
                _geoAnchorY = drawY;
            }
            else if (shadow)
            {
                Layer(s.Body, s.Group, s.Dir, frame, false, true, drawX, drawY, mirror, alpha, depth, mountOffset, over, 0, false, false, false, 0, charSitting, 0);
            }

            Layer(s.Body, s.Group, s.Dir, frame, false, false, drawX, drawY, mirror, alpha, depth, mountOffset, over, bodyHue, false, false, false, 0, charSitting, 0);

            int n = eq.Count;

            if (n != 0)
            {
                EmitEquipment(in s, in eq, n, drawX, drawY, mirror, alpha, depth, mountOffset, over, charSitting);
            }
            else
            {
                for (int i = 0; i < eq.LightCount; i++)
                {
                    AddLight(eq.LightItems[i], drawX, drawY);
                }
            }

            // MobileView.Draw's final FrameInfo transform
            _fiX = Math.Abs(_fiX);
            _fiY = Math.Abs(_fiY);
            _fiW += _fiX;
            _fiH += _fiY;
        }

        private void EmitEquipment(in AnimationState s, in EquipmentSlots eq, int n, int drawX, int drawY, bool mirror, float alpha, float depth, int mountOffset, ushort over, bool charSitting)
        {
            byte frame = s.Frame;

            // the facing-dependent moves on the slot indices: the layering rules data's direction rules (the rebuild's
            // interned per-facing orders; the cloak move with CloakAnchor only when that table is full)
            Span<byte> order = stackalloc byte[EquipmentSlots.MAX];
            n = AnimationRules.PaintOrder(in eq, s.Facing, order);

            for (int i = 0; i < n; i++)
            {
                // a light-only item lights where its layer sits among the drawn ones (the view's order matters when the scene's
                // light limit cuts the list)
                for (int k = 0; k < eq.LightCount; k++)
                {
                    if (eq.LightBefore[k] == order[i])
                    {
                        AddLight(eq.LightItems[k], drawX, drawY);
                    }
                }

                ref readonly EquipSlot slot = ref eq.Slots[order[i]];
                byte group = slot.Group == 0xFF ? s.Group : slot.Group;

                Layer
                (
                    slot.DrawBody,
                    group,
                    s.Dir,
                    frame,
                    true,
                    false,
                    drawX,
                    drawY,
                    mirror,
                    alpha,
                    depth,
                    mountOffset,
                    over,
                    slot.Hue,
                    false,
                    true,
                    (slot.Bits & EquipSlotBits.PartialHue) != 0,
                    slot.ConvColor,
                    charSitting,
                    (slot.Bits & EquipSlotBits.Light) != 0 ? slot.Item : 0
                );
            }

            for (int k = 0; k < eq.LightCount; k++)
            {
                if (eq.LightBefore[k] >= n)
                {
                    AddLight(eq.LightItems[k], drawX, drawY);
                }
            }
        }

        /// <summary>The frames of (body, action, stored dir) as Animations.GetAnimationFrames resolves them.</summary>
        public bool TryGetFrames(ushort id, byte action, byte dir, bool isEquip, out uint first, out int count, out ushort hueFromFile) =>
            TryGetFrames(_anims, id, action, dir, isEquip, false, out first, out count, out hueFromFile);

        /// <summary>As Animations.GetAnimationFrames(id, action, dir, isEquip, isCorpse) resolves them, from the tables only.</summary>
        public static bool TryGetFrames(AnimationsLoader anims, ushort id, byte action, byte dir, bool isEquip, bool isCorpse, out uint first, out int count, out ushort hueFromFile)
        {
            first = 0;
            count = 0;
            hueFromFile = 0;

            if (action >= AnimationsLoader.MAX_ACTIONS || dir >= AnimationsLoader.MAX_DIRECTIONS || id >= anims.BodyCount)
            {
                return false;
            }

            id = anims.ResolveBody(id, isCorpse, out hueFromFile);
            var type = (AnimationGroupsType)anims.GetBody(id).Type;

            if (isEquip && type != AnimationGroupsType.Equipment && type != AnimationGroupsType.Human)
            {
                return false;
            }

            if (!anims.TryGetDirection(id, action, dir + 3, out AnimDirEntry entry))
            {
                return false;
            }

            first = entry.FirstFrame;
            count = entry.FrameCount;

            return count > 0;
        }

        // MobileView.DrawInternal
        private void Layer
        (
            ushort id,
            byte group,
            byte dir,
            byte frameIndex,
            bool isEquip,
            bool isShadow,
            int x,
            int y,
            bool mirror,
            float alpha,
            float depth,
            int mountOffset,
            ushort overrideHue,
            ushort layerHue,
            bool isMount,
            bool isItem,
            bool itemPartial,
            ushort convColor,
            bool charSitting,
            uint lightItem
        )
        {
            if (id >= _anims.BodyCount || !TryGetFrames(id, group, dir, isEquip, out uint first, out int count, out ushort hueFromFile))
            {
                return;
            }

            if (frameIndex >= count)
            {
                frameIndex = (byte)(count - 1);
            }

            uint index = first + frameIndex;
            ref readonly AnimFrameEntry fe = ref _anims.PackFrames[index];
            _selBody = id;
            _selGroup = group;
            _selDir = dir;
            _selFrame = frameIndex;
            _selEquip = isEquip;
            int w = fe.Sprite.OrigW, h = fe.Sprite.OrigH;

            // no texture: nothing is drawn (the seated body's SKIP path draws nothing either)
            if (w == 0 || h == 0)
            {
                return;
            }

            if (_atlas != null)
            {
                Span<SpriteInfo> atlasFrames = _atlas.Frames(id, group, dir, isEquip, false);

                if (frameIndex >= atlasFrames.Length || atlasFrames[frameIndex].Texture == null)
                {
                    _atlasMiss = true;

                    return;
                }
            }

            int anchorX = x, anchorY = y;
            x -= mirror ? w - fe.CenterX : fe.CenterX;
            y -= h + fe.CenterY;

#if CUO_DEVTOOLS
            if (Scenes.ClipDump.Active && !isShadow)
            {
                Scenes.ClipDump.Layer(_objX, id, group, dir, frameIndex, isMount, isEquip, w, h, fe.CenterX, fe.CenterY, anchorX, anchorY, x, y, mountOffset, depth);
            }

            if (Scenes.ClipOrderCheck.Armed && !isShadow && _objStill && !_selectOnly)
            {
                // the strips' adds as drawn below (mobile_wall_order caps them), so the check's key order is the drawn one
                Span<(int Y, int H)> cs = stackalloc (int, int)[Scenes.ClipOrderCheck.MaxStrips];
                Span<float> adds = stackalloc float[Scenes.ClipOrderCheck.MaxStrips];
                int cn = Math.Min(AnimationRules.Strips(h, fe.CenterY, mountOffset, cs), cs.Length);

                for (int i = 0; i < cn; i++)
                {
                    adds[i] = StripAdd(i, x, w, depth);
                }

                Scenes.ClipOrderCheck.Layer(_objX, _objY, _objZ, id, _geoAnchorX, _geoAnchorY, x, y, w, h, fe.CenterY, mountOffset, depth, adds.Slice(0, cn));
            }
#endif

            if (!isShadow)
            {
                // MobileView.DrawInternal: the frame's bounds relative to the body anchor, and the drawn item's light
                int xx = mirror ? -(w - fe.CenterX) : -fe.CenterX;
                int yy = -(h + fe.CenterY + 3);
                _fiX = Math.Min(_fiX, xx);
                _fiY = Math.Min(_fiY, yy);
                _fiW = Math.Max(_fiW, xx + w);
                _fiH = Math.Max(_fiH, yy + h);

                if (lightItem != 0)
                {
                    AddLight(lightItem, mirror ? x + w : x, y);
                }
            }

            if (isShadow)
            {
                // mobile_wall_order: the shadow (sheared right by up to half its height) loses to the pieces it overlaps
                if (_wall.Count != 0 && !_submerged &&
                    Scenes.MobileWallOrder.ShadowAdd(in _wall, x - _wallRefX, x - _wallRefX + w + h / 2, depth, out float shadowAdd, out int shadowWall))
                {
#if CUO_DEVTOOLS
                    if (Scenes.MobileWallOrder.Check)
                    {
                        Scenes.MobileWallOrder.NoteMoved(in _wall, shadowWall, _objX, _objY, _objZ, id, -1, h, x - _wallRefX, x - _wallRefX + w + h / 2, depth, -1f, shadowAdd);
                    }
#endif
                    if (_wall.Active)
                    {
                        Write(index, x, y, 0, h, new Vector3(0, ShaderHueTranslator.SHADER_SHADOW, 1f), mirror, depth, 1, shadowAdd);

                        return;
                    }
                }

                Write(index, x, y, 0, h, new Vector3(0, ShaderHueTranslator.SHADER_SHADOW, 1f), mirror, depth, 0, 0f);

                return;
            }

            ushort hue = AnimationRules.LayerHue(overrideHue, layerHue, isMount, isItem, itemPartial, hueFromFile, convColor, out bool partial);
            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, alpha);

            if (charSitting)
            {
                // MobileView: CalculateSitAnimation then DrawCharacterSitted's sections
                Vector3 mod = SitDeform.Mod(ref _sitRows, y, h, isItem, _human);
                Write(index, x, y, 0, h, hueVec, mirror, depth, 1, 0f, true, mod);

                return;
            }

            Span<(int Y, int H)> strips = stackalloc (int, int)[Math.Min(AnimationRules.MaxStrips(h), MAX_STRIPS)];
            int n = AnimationRules.Strips(h, fe.CenterY, mountOffset, strips);

            for (int i = 0; i < n; i++)
            {
                if (strips[i].H > 0)
                {
                    float add = StripAdd(i, x, w, depth);
#if CUO_DEVTOOLS
                    if (Scenes.MobileWallOrder.Check && _wall.Count != 0 && !_submerged)
                    {
                        StripColumns(x, w, out int clo, out int chi);
                        Scenes.MobileWallOrder.CapAdd(i * 2, in _wall, clo, chi, depth, i == 0, out int wall);
                        float moved = WallRule(i, x, w, depth, i * 2);

                        if (moved != i * 2)
                        {
                            Scenes.MobileWallOrder.NoteMoved(in _wall, Math.Max(wall, 0), _objX, _objY, _objZ, id, i, strips[i].H, clo, chi, depth, i * 2, moved);
                        }
                    }
#endif
                    if (i == 0 && _wall.Count != 0 && !_submerged && OverheadRows(x, w, y + strips[0].Y, strips[0].H, depth, add, id, out int topRows, out float topAdd))
                    {
                        // mobile_wall_order: the upper strip's rows under a piece overhead behind it, the rest as before
                        Write(index, x, y + strips[0].Y, strips[0].Y, topRows, hueVec, mirror, depth, 1, topAdd);

                        if (strips[0].H > topRows)
                        {
                            Write(index, x, y + strips[0].Y + topRows, strips[0].Y + topRows, strips[0].H - topRows, hueVec, mirror, depth, 1, add);
                        }

                        continue;
                    }

                    Write(index, x, y + strips[i].Y, strips[i].Y, strips[i].H, hueVec, mirror, depth, 1, add);
                }
            }
        }

        // the add of strip i (DepthOf kind 1): + 2i, + 0 under a wet surface (wet_surface_depth), and with mobile_wall_order the
        // strips below the anchor capped just behind a piece ahead that the composite's columns overlap, the upper strip just
        // behind a piece overhead (and mid-step behind a piece ahead of the tile it leaves) (MobileWallOrder.CapAdd)
        private float StripAdd(int i, int x, int w, float depth)
        {
            if (_submerged)
            {
                return 0;
            }

            float add = i * 2;

            if (_wall.Active && _wall.Count != 0)
            {
                add = WallRule(i, x, w, depth, add);
            }

            return add;
        }

        // mobile_wall_order: raised over the stairs under the mobile (round 3), then capped behind the pieces covering it
        private float WallRule(int i, int x, int w, float depth, float add)
        {
            StripColumns(x, w, out int lo, out int hi);

            if (Scenes.MobileWallOrder.FloorAdd(in _wall, lo, hi, depth, out float raise) && raise > add)
            {
                add = raise;
            }

            return Scenes.MobileWallOrder.CapAdd(add, in _wall, lo, hi, depth, i == 0, out _);
        }

        // mobile_wall_order: the rows of the upper strip (screen rows [top, top + h)) a piece overhead covers, and their add
        private bool OverheadRows(int x, int w, int top, int h, float depth, float add, ushort id, out int rows, out float topAdd)
        {
            rows = 0;
            StripColumns(x, w, out int lo, out int hi);

            if (!Scenes.MobileWallOrder.OverheadSplit(in _wall, lo, hi, depth, add, out int splitRow, out topAdd, out int wall))
            {
                return false;
            }

            rows = Math.Min(h, _wallRefY + splitRow - top);

            if (rows <= 0)
            {
                return false;
            }

#if CUO_DEVTOOLS
            if (Scenes.MobileWallOrder.Check)
            {
                Scenes.MobileWallOrder.NoteMoved(in _wall, wall, _objX, _objY, _objZ, id, 0, rows, lo, hi, depth, add, topAdd);
            }
#endif

            return _wall.Active;
        }

        // mobile_wall_order: a strip's columns relative to the depth tile's centre: the composite's (MobileWallOrder.Scan.SpanLo,
        // the last FrameInfo, so every layer gets one cap), else the layer's frame [x, x + w)
        private void StripColumns(int x, int w, out int lo, out int hi)
        {
            if (_wall.SpanHi > _wall.SpanLo)
            {
                lo = _geoAnchorX + _wall.SpanLo - _wallRefX;
                hi = _geoAnchorX + _wall.SpanHi - _wallRefX;
            }
            else
            {
                lo = x - _wallRefX;
                hi = x - _wallRefX + w;
            }
        }

        private void AddLight(uint item, int x, int y)
        {
            if (_lightCount >= _lights.Length)
            {
                Array.Resize(ref _lights, _lights.Length * 2);
            }

            _lights[_lightCount++] = new LightRequest { Item = item, X = x, Y = y };
        }

        // GpuWorldRenderer.Emit for an animation frame drawn from source rows [srcY, srcY + srcH) at (px, py). The depth is
        // baseDepth (kind 0) or (baseDepth + 1f) + add (kind 1): the shadow, the seated body and the strips' depth + 1f + i * 2.
        // T7b: the record of that draw (select), resolved at once to instances unless only selecting. The selection fields
        // (_selBody…) are the current Layer's.
        private void Write(uint index, int px, int py, int srcY, int srcH, Vector3 hue, bool flip, float baseDepth, byte kind, float add,
            bool sitted = false, Vector3 sitMod = default)
        {
            var sprite = new DynamicSprite
            {
                Kind = DynamicSpriteKind.Animation,
                Flags = (flip ? DynamicSpriteFlags.Flip : 0) | (sitted ? DynamicSpriteFlags.Sitted : 0) | (_selEquip ? DynamicSpriteFlags.Equip : 0),
                Rank = RANK_MOBILE,
                Body = _selBody,
                Group = _selGroup,
                Dir = _selDir,
                Frame = _selFrame,
                Id = index,
                X = px,
                Y = py,
                SrcY = srcY,
                SrcH = srcH,
                Hue = hue,
                Depth = DepthOf(baseDepth, kind, add),
                ObjX = _objX,
                SitMod = sitMod
            };

            // clip-2: only with depth_model on, so the default path's records and instances stay as before the switch existed
            if (_geo)
            {
                sprite.GeoAx = (short)Math.Clamp(_geoAnchorX - px, short.MinValue, short.MaxValue);
                sprite.GeoAy = (short)Math.Clamp(_geoAnchorY - py, short.MinValue, short.MaxValue);
                sprite.GeoZ = _objZ;
                sprite.Geo = true;
            }

            if (_selectOnly)
            {
                if (_selCount >= _sel.Length)
                {
                    Array.Resize(ref _sel, _sel.Length * 2);
                    Array.Resize(ref _selKind, _sel.Length);
                    Array.Resize(ref _selAdd, _sel.Length);
                }

                _selKind[_selCount] = kind;
                _selAdd[_selCount] = add;
                _sel[_selCount++] = sprite;

                return;
            }

            int before = _count;
            GpuSpriteResolve.Resolve(in _tables, in sprite, _offsetX, _offsetY, ref _out, ref _count);

            if (_record != null)
            {
                for (int c = before; c < _count; c++)
                {
                    int i = c - _recordStart;

                    if (i >= _recKind.Length)
                    {
                        Array.Resize(ref _recKind, Math.Max(i + 1, _recKind.Length * 2));
                        Array.Resize(ref _recAdd, _recKind.Length);
                    }

                    _recKind[i] = kind;
                    _recAdd[i] = add;
                }
            }
        }

        // the seated body's waist and knee rows for its equipment (MobileView's statics), and whether the mobile is human
        private SitDeform.BodyRows _sitRows;
        private bool _human;

        // clip-2 depth model: the mobile's composite anchor (all its layers) and its z (DynamicSprite.GeoAx/GeoAy/GeoZ)
        private int _geoAnchorX, _geoAnchorY;
        private sbyte _objZ;

        // clip-2: depth_model (DepthModel.On) at Begin; MobileFrameCache.Geo keys recorded instances on it
        private bool _geo;

        // wet_surface_depth (key mode): the mobile is under a wet surface (MobileFrameCache.Submerged, part of the select key),
        // so every strip takes the upper strip's + 1 and the water ahead hides the feet with the legs
        private bool _submerged;

        // mobile_wall_order (key mode): the mobile's wall scan (MobileFrameCache.Wall, part of the select key) and the screen
        // column of its depth tile's centre, which the scan's wall columns are relative to
        private Scenes.MobileWallOrder.Scan _wall;
        private int _wallRefX, _wallRefY;
#if CUO_DEVTOOLS
        // CUO_CLIP_ORDER_CHECK: the mobile's tile y and whether it stands still (ClipOrderCheck.Layer)
        private int _objY;
        private bool _objStill;
#endif
        private ushort _selBody;
        private byte _selGroup, _selDir, _selFrame;
        private bool _selEquip;

        internal static float DepthOf(float baseDepth, byte kind, float add) => kind == 0 ? baseDepth : baseDepth + 1f + add;

        private MobileFrameCache _record;
        private int _recordStart;

        // the depth recipe of each instance being recorded, copied into the cache's entries when the recording commits (a
        // held look keeps the previous entries: asset_prefetch_on_packet)
        private byte[] _recKind = new byte[16];
        private float[] _recAdd = new float[16];

        /// <summary>The page residency of the Gpu world the instances go to (GpuWorldRenderer sets it before Emit): decides
        /// whether a new look is complete (asset_prefetch_on_packet). Null: not checked.</summary>
        internal IDynamicPageResidency PageResidency;

        /// <summary>
        /// W5 (MobileFrameCache): <see cref="Emit(EcsEntity, int, int, float)"/>, or the instances, FrameInfo and
        /// lights <paramref name="cache"/> recorded when the selection (<see cref="MobileFrameCache.SelectGen"/>), alpha,
        /// override hue, body hue and pack are the same, moved to this frame's position, walking offset and depth.
        /// </summary>
        public void Emit(EcsEntity e, int posX, int posY, float depth, MobileFrameCache cache)
        {
            EcsRow data = EcsRead.Row(e);
            Emit(e, data, in data.Get<AnimationState>(), in data.Get<MobileRenderState>(), posX, posY, depth, cache);
        }

        /// <summary>
        /// <see cref="Emit(EcsEntity, int, int, float, MobileFrameCache)"/> with the entity's <see cref="AnimationState"/> and
        /// <see cref="MobileRenderState"/> already in hand (E5). They must be <paramref name="e"/>'s own, current components.
        /// </summary>
        public void Emit(EcsEntity e, in AnimationState s, in MobileRenderState rs, int posX, int posY, float depth, MobileFrameCache cache) =>
            Emit(e, EcsRead.Row(e), in s, in rs, posX, posY, depth, cache);

        /// <summary>
        /// <see cref="Emit(EcsEntity, in AnimationState, in MobileRenderState, int, int, float, MobileFrameCache)"/> with
        /// <paramref name="e"/>'s record already read (perf-11: the draw reads it once for every component).
        /// </summary>
        public void Emit(EcsEntity e, in EcsRow data, in AnimationState s, in MobileRenderState rs, int posX, int posY, float depth, MobileFrameCache cache)
        {
            _submerged = cache != null && cache.Submerged;
            _wall = cache != null ? cache.Wall : default;

            if (cache == null || (s.Flags & AnimStateFlags.Visible) == 0 || !_hasAnims)
            {
                if (cache != null)
                {
                    cache.EmitValid = false;
                }

                Emit(e, data, posX, posY, depth);

                return;
            }

            ushort bodyHue = data.Get<Hue>().Value;
            int baseX = posX + rs.OffsetX, baseY = posY + rs.OffsetY;

            if (cache.EmitValid && cache.EmitSelectGen == cache.SelectGen && cache.Alpha == rs.Alpha && cache.OverrideHue == rs.OverrideHue &&
                cache.BodyHue == bodyHue && ReferenceEquals(cache.Pack, _pack) && cache.Geo == _geo
#if CUO_DEVTOOLS
                && !Scenes.ClipDump.Active // clip-1: the dump reads the layers as Layer draws them
                && !Scenes.ClipOrderCheck.Armed // clip-2: so does the order check
#endif
                )
            {
                MobileFrameCache.EmitHits++;
                // a pending view from an earlier emit since Begin aliases cache.Abs (the same cache replayed twice): move it
                // into _out before Replay mutates Abs, and before `start` is taken
                MaterializeView();
                int objX = data.Get<Position>().X;
                int lightStart = _lightCount;
                int start = _count;
                Replay(cache, baseX + _offsetX, baseY + _offsetY, depth, objX, baseX, baseY, data.Get<Position>().Z);

#if CUO_DEVTOOLS
                if (MobileAnimationSystem.CacheCheck)
                {
                    CheckReplay(e, posX, posY, depth, start, lightStart);
                }

                // CUO_LOOK_PREFETCH_CHECK: a recording made with pages missing draws incomplete until they are resident
                if (LookPrefetch.Check && cache.EmitIncomplete != 0 && PageResidency != null)
                {
                    int missing = MissingAnimEntries(cache);

                    if (missing != 0)
                    {
                        LookPrefetch.NoteIncomplete(missing, cache.Count, cache.EmitIncomplete == 1);
                    }
                    else
                    {
                        cache.EmitIncomplete = 0;
                    }
                }

                if (PageResidency != null)
                {
                    LookPrefetch.NoteDrawn(cache, cache.EmitIncomplete == 0);
                }
#endif

                return;
            }

            MaterializeView();
            MobileFrameCache.EmitMisses++;
            FrameStats.Inc(FrameCounter.MobEmitMisses);
            int recStart = _count;
            int recLights = _lightCount;
            _record = cache;
            _recordStart = recStart;

            try
            {
                Emit(e, data, posX, posY, depth);
            }
            finally
            {
                _record = null;
            }

            int n = _count - recStart;
            bool hold = LookPrefetch.HoldEnabled;

            if ((hold || LookPrefetch.Check) && PageResidency != null)
            {
                int missing = MissingAnimPages(_out, recStart, n, hold);

                if (missing != 0)
                {
                    bool previous = cache.EmitValid && cache.Count != 0 && ReferenceEquals(cache.Pack, _pack) && cache.Geo == _geo;

                    if (hold && previous && HoldLook(cache))
                    {
                        // asset_prefetch_on_packet: the previous complete look until every page of the new one is resident
                        _count = recStart;
                        _lightCount = recLights;
                        Replay(cache, baseX + _offsetX, baseY + _offsetY, depth, data.Get<Position>().X, baseX, baseY, data.Get<Position>().Z);
                        LookPrefetch.FrameHeld++;
#if CUO_DEVTOOLS
                        LookPrefetch.NoteDrawn(cache, false);
#endif

                        return;
                    }

                    if (hold && !previous && LookPrefetch.Enabled && FirstSightWait(cache))
                    {
                        // first sight: nothing until the body and every equipment page are resident (bounded)
                        _count = recStart;
                        _lightCount = recLights;
                        LookPrefetch.FrameFirstSightWait++;
#if CUO_DEVTOOLS
                        LookPrefetch.NoteDrawn(cache, false);
#endif

                        return;
                    }

                    LookPrefetch.NoteIncomplete(missing, n, previous);
                    cache.EmitIncomplete = previous ? (byte)1 : (byte)2;
#if CUO_DEVTOOLS
                    LookPrefetch.NoteDrawn(cache, false);
#endif
                }
                else
                {
                    if (hold)
                    {
                        LookComplete(cache);
                    }
#if CUO_DEVTOOLS
                    LookPrefetch.NoteDrawn(cache, true);
#endif

                    cache.HoldSince = 0;
                    cache.EmitIncomplete = 0;
                }
            }

            cache.EnsureCapacity(n);

            for (int i = 0; i < n; i++)
            {
                SpriteInstance inst = _out[recStart + i];
                inst.X -= baseX + _offsetX;
                inst.Y -= baseY + _offsetY;
                ref MobileFrameCache.Entry entry = ref cache.Entries[i];
                entry.Instance = inst;
                entry.Kind = _recKind[i];
                entry.Add = _recAdd[i];
            }

            cache.Count = n;
            cache.AbsValid = false;
            cache.FrameInfo = FrameInfo;
            int ln = _lightCount - recLights;

            if (cache.Lights.Length < ln)
            {
                Array.Resize(ref cache.Lights, ln);
            }

            for (int i = 0; i < ln; i++)
            {
                LightRequest l = _lights[recLights + i];
                l.X -= baseX;
                l.Y -= baseY;
                cache.Lights[i] = l;
            }

            cache.LightCount = ln;
            cache.EmitSelectGen = cache.SelectGen;
            cache.Alpha = rs.Alpha;
            cache.OverrideHue = rs.OverrideHue;
            cache.BodyHue = bodyHue;
            cache.Pack = _pack;
            cache.Geo = _geo;
            cache.EmitValid = true;
        }

        /// <summary>
        /// Replays <paramref name="cache"/> at screen offset (dx, dy) = (draw origin + the batch offset), base depth
        /// <paramref name="depth"/> and tile x <paramref name="objX"/>. <see cref="MobileFrameCache.Abs"/> keeps the last
        /// replay in absolute coordinates, tagged with the (dx, dy, depth, objX) it was made for:
        /// <list type="bullet">
        /// <item>same tag (a standing mobile, still camera): nothing to compute;</item>
        /// <item>same depth and tile x, other (dx, dy) (the camera scrolled, a walking offset changed): X and Y shifted by
        /// the difference — integer adds, so the result is exactly what a full recompute gives;</item>
        /// <item>otherwise (depth or tile x changed, or first replay since the entries were recorded): rebuilt from the
        /// relative entries, per instance, with the same depth key calculation as the extract.</item>
        /// </list>
        /// The instances are then exposed as a view when this is the first thing emitted since Begin, else appended to _out.
        /// </summary>
        // asset_prefetch_on_packet: the instances in [start, start + n) whose animation page is not resident (Gpu draws them as
        // nothing); request: queue those pages (a held look draws none of them, so the renderer would not request them)
        private int MissingAnimPages(SpriteInstance[] instances, int start, int n, bool request)
        {
            IDynamicPageResidency pages = PageResidency;
            int missing = 0;

            for (int i = start; i < start + n; i++)
            {
                ref readonly SpriteInstance inst = ref instances[i];

                if (!pages.Resident(in inst))
                {
                    missing++;

                    if (request)
                    {
                        pages.Request(in inst);
                    }
                }
            }

            return missing;
        }

#if CUO_DEVTOOLS
        private int MissingAnimEntries(MobileFrameCache cache)
        {
            int missing = 0;

            for (int i = 0; i < cache.Count; i++)
            {
                if (!PageResidency.Resident(in cache.Entries[i].Instance))
                {
                    missing++;
                }
            }

            return missing;
        }
#endif

        // asset_prefetch_on_packet: whether the previous look may still be drawn (LookPrefetch.HoldMaxMs from the first held
        // frame)
        private static bool HoldLook(MobileFrameCache cache)
        {
            if (cache.HoldCapped)
            {
                return false;
            }

            uint now = LookPrefetch.Now;

            if (cache.HoldSince == 0)
            {
                cache.HoldSince = now == 0 ? 1u : now;
            }

            if (now - cache.HoldSince <= LookPrefetch.HoldMaxMs)
            {
                LookPrefetch.ScheduleBound(cache.HoldSince, LookPrefetch.HoldMaxMs);

                return true;
            }

            cache.HoldCapped = true;
            LookPrefetch.NoteHoldEnd(now - cache.HoldSince, true);

            return false;
        }

        // asset_prefetch_on_packet: whether a mobile never drawn complete is still drawn as nothing (LookPrefetch.FirstSightMaxMs
        // from the first frame it would be visible)
        private static bool FirstSightWait(MobileFrameCache cache)
        {
            if (cache.EverComplete || cache.FirstSightCapped)
            {
                return false;
            }

            uint now = LookPrefetch.Now;

            if (cache.FirstSightSince == 0)
            {
                cache.FirstSightSince = now == 0 ? 1u : now;
            }

            if (now - cache.FirstSightSince <= LookPrefetch.FirstSightMaxMs)
            {
                LookPrefetch.ScheduleBound(cache.FirstSightSince, LookPrefetch.FirstSightMaxMs);

                return true;
            }

            cache.FirstSightCapped = true;
            LookPrefetch.NoteFirstSightEnd(now - cache.FirstSightSince, true);

            return false;
        }

        // the whole look was loaded at this draw: a hold or first-sight wait in progress ends
        private static void LookComplete(MobileFrameCache cache)
        {
            if (cache.HoldSince != 0)
            {
                if (!cache.HoldCapped)
                {
                    LookPrefetch.NoteHoldEnd(LookPrefetch.Now - cache.HoldSince, false);
                }

                cache.HoldSince = 0;
                cache.HoldCapped = false;
            }

            if (!cache.EverComplete)
            {
                cache.EverComplete = true;

                if (!cache.FirstSightCapped)
                {
                    LookPrefetch.NoteFirstSightEnd(cache.FirstSightSince == 0 ? 0 : LookPrefetch.Now - cache.FirstSightSince, false);
                }
            }
        }

        internal void Replay(MobileFrameCache cache, int dx, int dy, float depth, int objX, int baseX, int baseY, sbyte z)
        {
            // a pending view aliases cache.Abs when this cache was replayed earlier since Begin: keep it before Abs changes
            MaterializeView();
            int n = cache.Count;
            SpriteInstance[] abs = cache.Abs;

            if (!cache.AbsValid || abs.Length < n)
            {
                if (abs.Length < n)
                {
                    abs = cache.Abs = new SpriteInstance[Math.Max(n, 16)];
                }

                RebuildAbs(cache, abs, n, dx, dy, depth, objX, z);
            }
            else if (cache.AbsDepth != depth || cache.AbsObjX != objX)
            {
                RebuildAbs(cache, abs, n, dx, dy, depth, objX, z);
            }
            else if (cache.AbsDx != dx || cache.AbsDy != dy)
            {
                int ddx = dx - cache.AbsDx, ddy = dy - cache.AbsDy;

                for (int i = 0; i < n; i++)
                {
                    ref SpriteInstance inst = ref abs[i];
                    inst.X += ddx;
                    inst.Y += ddy;
                }

                cache.AbsDx = dx;
                cache.AbsDy = dy;
            }

            if (_count == 0 && _viewArr == null)
            {
                _viewArr = abs;
                _viewCount = n;
            }
            else
            {
                MaterializeView();

                if (_count + n > _out.Length)
                {
                    Array.Resize(ref _out, Math.Max(_out.Length * 2, _count + n));
                }

                Array.Copy(abs, 0, _out, _count, n);
                _count += n;
            }

            Rectangle fi = cache.FrameInfo;
            _fiX = fi.X;
            _fiY = fi.Y;
            _fiW = fi.Width;
            _fiH = fi.Height;

            for (int i = 0; i < cache.LightCount; i++)
            {
                LightRequest l = cache.Lights[i];
                AddLight(l.Item, l.X + baseX, l.Y + baseY);
            }
        }

        private static void RebuildAbs(MobileFrameCache cache, SpriteInstance[] abs, int n, int dx, int dy, float depth, int objX, sbyte z)
        {
            // only records made with depth_model on carry anchors (MobileFrameCache.Geo)
            bool geo = cache.Geo;

            for (int i = 0; i < n; i++)
            {
                ref readonly MobileFrameCache.Entry entry = ref cache.Entries[i];
                ref SpriteInstance inst = ref abs[i];
                inst = entry.Instance;
                inst.X += dx;
                inst.Y += dy;
                inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(DepthOf(depth, entry.Kind, entry.Add) * 100f) - 50, objX, RANK_MOBILE);

                // clip-2: the recorded layers carry the z of their record; a z change also changes the depth, so it lands here
                if (geo && inst.TryGeoMobile(out int adx, out int ady, out int rz) && rz != z)
                {
                    inst.GeoMobile(adx, ady, z);
                }
            }

            cache.AbsValid = true;
            cache.AbsDx = dx;
            cache.AbsDy = dy;
            cache.AbsDepth = depth;
            cache.AbsObjX = objX;
        }

        /// <summary>
        /// The Legacy sink (LegacySpriteSink): the Legacy batcher calls of <see cref="Select(EcsEntity, in EcsRow, int, int, float)"/> resolved
        /// through <paramref name="src"/> and drawn, or the calls <paramref name="cache"/> recorded for the same selection,
        /// alpha, override hue, body hue and source, moved to this frame's draw origin and base depth (as the Gpu path's
        /// <see cref="Emit(EcsEntity, in EcsRow, in AnimationState, in MobileRenderState, int, int, float, MobileFrameCache)"/>).
        /// A replay skips the layer walk, the frame lookups of the select and of the resolve, and the records. FrameInfo and
        /// <see cref="Lights"/> are set either way; <see cref="Selected"/> is empty after a replay. The state must be
        /// <paramref name="e"/>'s current components (after MobileAnimationSystem.UpdateInPlace). CUO_MOBILE_CACHE_CHECK=1
        /// compares every replay with a fresh select and resolve.
        /// </summary>
        internal void DrawLegacy(UltimaBatcher2D batcher, ILegacySpriteSource src, EcsEntity e, in EcsRow data, in AnimationState s,
            in MobileRenderState rs, int posX, int posY, float depth, MobileFrameCache cache)
        {
            BeginSelect(src);
            _submerged = cache != null && cache.Submerged;
            _wall = cache != null ? cache.Wall : default;

            if (cache == null || (s.Flags & AnimStateFlags.Visible) == 0 || !_hasAnims)
            {
                if (cache != null)
                {
                    cache.LegacyValid = false;
                }

                Select(e, data, posX, posY, depth);
                LegacySpriteResolve.DrawAll(batcher, src, Selected);

                return;
            }

            ushort bodyHue = data.Get<Hue>().Value;
            int baseX = posX + rs.OffsetX, baseY = posY + rs.OffsetY;

            if (cache.LegacyValid && cache.LegacySelectGen == cache.SelectGen && cache.LegacyAlpha == rs.Alpha &&
                cache.LegacyOverrideHue == rs.OverrideHue && cache.LegacyBodyHue == bodyHue && ReferenceEquals(cache.LegacySource, src) &&
                cache.LegacyAtlasEpoch == ClassicUO.Renderer.Animations.Animations.AtlasEpoch)
            {
                MobileFrameCache.LegacyHits++;
                _selCount = 0;
                ReplayLegacy(batcher, cache, baseX, baseY, depth);
#if CUO_DEVTOOLS
                // a Legacy recording is made only with every texture present (record needs !_atlasMiss)
                LookPrefetch.NoteDrawn(cache, true);
#endif

#if CUO_DEVTOOLS
                if (MobileAnimationSystem.CacheCheck)
                {
                    CheckLegacyReplay(src, e, in data, cache, posX, posY, depth, baseX, baseY);
                }
#endif

                return;
            }

            MobileFrameCache.LegacyMisses++;
            FrameStats.Inc(FrameCounter.MobLegacyMisses);
            Select(e, data, posX, posY, depth);
            bool hold = LookPrefetch.HoldEnabled;

            if (_atlasMiss && (hold || LookPrefetch.Check))
            {
                bool previous = cache.LegacyValid && cache.LegacyCount != 0 && ReferenceEquals(cache.LegacySource, src) &&
                    cache.LegacyAtlasEpoch == ClassicUO.Renderer.Animations.Animations.AtlasEpoch;

                if (hold && previous && HoldLook(cache))
                {
                    // asset_prefetch_on_packet: the previous complete look until every direction of the new one is uploaded
                    // (the select above started their decodes)
                    _selCount = 0;
                    ReplayLegacy(batcher, cache, baseX, baseY, depth);
                    LookPrefetch.FrameHeld++;
#if CUO_DEVTOOLS
                    LookPrefetch.NoteDrawn(cache, false);
#endif

                    return;
                }

                if (hold && !previous && LookPrefetch.Enabled && FirstSightWait(cache))
                {
                    // first sight: nothing until the body and every equipment direction are uploaded (bounded)
                    _selCount = 0;
                    LookPrefetch.FrameFirstSightWait++;
#if CUO_DEVTOOLS
                    LookPrefetch.NoteDrawn(cache, false);
#endif

                    return;
                }

                LookPrefetch.NoteIncomplete(_selCount == 0 ? 1 : 0, 1, previous);
            }

#if CUO_DEVTOOLS
            LookPrefetch.NoteDrawn(cache, !_atlasMiss);
#endif

            if (!_atlasMiss && hold)
            {
                LookComplete(cache);
            }
            else if (!_atlasMiss)
            {
                cache.HoldSince = 0;
            }

            // a seated mobile's equipment reads the body rows the last seated human body left (MobileView's statics): not
            // replayed. asset_prefetch_on_packet: recorded also when not reusable (a walking mobile), as the look to hold.
            bool record = (cache.SelectValid || hold) && !_atlasMiss && (s.Flags & AnimStateFlags.CharSitting) == 0;

            if (record)
            {
                cache.EnsureLegacyCapacity(_selCount);
            }

            int k = 0;

            for (int i = 0; i < _selCount; i++)
            {
                ref readonly DynamicSprite sprite = ref _sel[i];

                if (!LegacySpriteResolve.Resolve(src, in sprite, out LegacyDraw d))
                {
                    record = false;

                    continue;
                }

                LegacySpriteResolve.Draw(batcher, in d);
                record &= d.Mode != SpriteCaptureMode.Sitted && !d.Rotated;

                if (record)
                {
                    ref MobileFrameCache.LegacyEntry entry = ref cache.LegacyEntries[k++];
                    entry.Texture = d.Texture;
                    entry.Source = d.Source;
                    entry.Hue = d.Hue;
                    entry.Flip = d.Flip;
                    entry.Mode = d.Mode;
                    entry.X = sprite.X - baseX;
                    entry.Y = sprite.Y - baseY;
                    entry.Kind = _selKind[i];
                    entry.Add = _selAdd[i];
                }
            }

            if (!record)
            {
                cache.LegacyValid = false;

                return;
            }

            cache.LegacyCount = k;
            cache.LegacyFrameInfo = FrameInfo;

            if (cache.LegacyLights.Length < _lightCount)
            {
                Array.Resize(ref cache.LegacyLights, _lightCount);
            }

            for (int i = 0; i < _lightCount; i++)
            {
                LightRequest l = _lights[i];
                l.X -= baseX;
                l.Y -= baseY;
                cache.LegacyLights[i] = l;
            }

            cache.LegacyLightCount = _lightCount;
            cache.LegacySelectGen = cache.SelectGen;
            cache.LegacyAtlasEpoch = ClassicUO.Renderer.Animations.Animations.AtlasEpoch;
            cache.LegacyAlpha = rs.Alpha;
            cache.LegacyOverrideHue = rs.OverrideHue;
            cache.LegacyBodyHue = bodyHue;
            cache.LegacySource = src;
            cache.LegacyValid = true;
        }

        // the Legacy batcher calls the cache recorded, at draw origin (baseX, baseY) and base depth, with FrameInfo and lights
        private void ReplayLegacy(UltimaBatcher2D batcher, MobileFrameCache cache, int baseX, int baseY, float depth)
        {
            MobileFrameCache.LegacyEntry[] entries = cache.LegacyEntries;

            for (int i = 0; i < cache.LegacyCount; i++)
            {
                // LegacySpriteResolve.Draw of LegacyReplayed's call, from the entry's fields (recorded entries are
                // normal or shadow draws, never seated or rotated)
                ref readonly MobileFrameCache.LegacyEntry entry = ref entries[i];
                var position = new Vector2(baseX + entry.X, baseY + entry.Y);
                // anim_atlas_lru: a replay skips GetAnimationFrames, so it marks the page used itself
                ClassicUO.Renderer.Animations.AtlasPageClock.Touch(entry.Texture);
#if CUO_DEVTOOLS
                if (ClassicUO.Renderer.Animations.Animations.LruCheck)
                {
                    ClassicUO.Renderer.Animations.Animations.LruCheckUse(entry.Texture, "mobile replay");
                }
#endif
                float d = DepthOf(depth, entry.Kind, entry.Add);

                if (entry.Mode == SpriteCaptureMode.Shadow)
                {
                    batcher.DrawShadow(entry.Texture, position, entry.Source, entry.Flip, d);
                }
                else
                {
                    batcher.Draw(entry.Texture, position, entry.Source, entry.Hue, 0f, Vector2.Zero, 1f,
                        entry.Flip ? Microsoft.Xna.Framework.Graphics.SpriteEffects.FlipHorizontally : Microsoft.Xna.Framework.Graphics.SpriteEffects.None, d);
                }
            }

            ApplyLegacyEffects(cache, baseX, baseY);
        }

#if CUO_DEVTOOLS
        // entry i of the cache's Legacy calls at draw origin (baseX, baseY) and base depth: what the select + resolve give
        // there (integer positions; the depth is DepthOf's expression)
        private static LegacyDraw LegacyReplayed(MobileFrameCache cache, int i, int baseX, int baseY, float depth)
        {
            ref readonly MobileFrameCache.LegacyEntry entry = ref cache.LegacyEntries[i];

            return new LegacyDraw
            {
                Texture = entry.Texture,
                Position = new Vector2(baseX + entry.X, baseY + entry.Y),
                Source = entry.Source,
                Hue = entry.Hue,
                Depth = DepthOf(depth, entry.Kind, entry.Add),
                Flip = entry.Flip,
                Mode = entry.Mode
            };
        }

#endif

        private void ApplyLegacyEffects(MobileFrameCache cache, int baseX, int baseY)
        {
            Rectangle fi = cache.LegacyFrameInfo;
            _fiX = fi.X;
            _fiY = fi.Y;
            _fiW = fi.Width;
            _fiH = fi.Height;
            _lightCount = 0;

            for (int i = 0; i < cache.LegacyLightCount; i++)
            {
                LightRequest l = cache.LegacyLights[i];
                AddLight(l.Item, l.X + baseX, l.Y + baseY);
            }
        }

#if CUO_DEVTOOLS
        // the checker
        public static long CheckLegacyReplays, CheckLegacyReplayDiffs;

        // CUO_MOBILE_CACHE_CHECK: the replayed Legacy calls, FrameInfo and lights against a fresh select and resolve (not drawn);
        // the replay's FrameInfo and lights stand afterwards
        private void CheckLegacyReplay(ILegacySpriteSource src, EcsEntity e, in EcsRow data, MobileFrameCache cache, int posX, int posY,
            float depth, int baseX, int baseY)
        {
            // the replay's lights are already in _lights; the fresh select appends its own, so start it from none
            _lightCount = 0;
            Select(e, data, posX, posY, depth);
            CheckLegacyReplays++;
            string problem = null;

            if (FrameInfo != cache.LegacyFrameInfo)
            {
                problem = $" frameinfo {cache.LegacyFrameInfo} vs {FrameInfo}";
            }

            if (_lightCount != cache.LegacyLightCount)
            {
                problem += $" lights {cache.LegacyLightCount} vs {_lightCount}";
            }
            else
            {
                for (int i = 0; i < _lightCount; i++)
                {
                    LightRequest l = cache.LegacyLights[i];

                    if (l.Item != _lights[i].Item || l.X + baseX != _lights[i].X || l.Y + baseY != _lights[i].Y)
                    {
                        problem += $" light {i}";

                        break;
                    }
                }
            }

            int k = 0;

            for (int i = 0; i < _selCount; i++)
            {
                if (!LegacySpriteResolve.Resolve(src, in _sel[i], out LegacyDraw d))
                {
                    continue;
                }

                if (k >= cache.LegacyCount)
                {
                    k++;

                    continue;
                }

                LegacyDraw replayed = LegacyReplayed(cache, k, baseX, baseY, depth);

                if (problem == null && !replayed.Same(in d))
                {
                    problem = $" call {k}: replayed {replayed} | fresh {d}";
                }

                k++;
            }

            if (k != cache.LegacyCount)
            {
                problem += $" calls {cache.LegacyCount} vs {k}";
            }

            if (problem != null)
            {
                CheckLegacyReplayDiffs++;

                if (_checkLogged++ < 20)
                {
                    ClassicUO.Utility.Logging.Log.Warn($"[mobile-cache-check] legacy replay differs 0x{e.GetComponent<Serial>().Value:X8}:{problem}");
                }
            }

            _selCount = 0;
            ApplyLegacyEffects(cache, baseX, baseY);
        }

        public static long CheckReplays, CheckReplayDiffs;
        private static int _checkLogged;

        // CUO_MOBILE_CACHE_CHECK: the replayed instances (the view, or _out from `start`), FrameInfo and lights against a
        // fresh Emit (appended, compared, dropped)
        private void CheckReplay(EcsEntity e, int posX, int posY, float depth, int start, int lightStart)
        {
            // the replayed instances stay where they are: Emit appends after _count (a resize leaves the old array intact), so
            // they are compared in place, with no copy
            SpriteInstance[] replayedArr = _viewArr ?? _out;
            int replayedStart = _viewArr != null ? 0 : start;
            int replayedLen = _viewArr != null ? _viewCount : _count - start;
            SpriteInstance[] savedView = _viewArr;
            int savedViewCount = _viewCount, savedCount = _count;
            int replayLights = _lightCount;
            int fx = _fiX, fy = _fiY, fw = _fiW, fh = _fiH;

            _viewArr = null;
            Emit(e, posX, posY, depth);
            CheckReplays++;

            int freshStart = savedCount;
            bool same = _count - freshStart == replayedLen && _lightCount - replayLights == replayLights - lightStart &&
                        fx == _fiX && fy == _fiY && fw == _fiW && fh == _fiH;

            for (int i = 0; same && i < replayedLen; i++)
            {
                same = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<SpriteInstance>(replayedArr, replayedStart + i, 1))
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<SpriteInstance>(_out, freshStart + i, 1)));
            }

            for (int i = 0; same && i < replayLights - lightStart; i++)
            {
                LightRequest a = _lights[lightStart + i], b = _lights[replayLights + i];
                same = a.Item == b.Item && a.X == b.X && a.Y == b.Y;
            }

            if (!same)
            {
                CheckReplayDiffs++;

                if (_checkLogged++ < 20)
                {
                    ref readonly AnimationState s = ref e.GetComponent<AnimationState>();
                    ClassicUO.Utility.Logging.Log.Warn($"[mobile-cache-check] instances differ 0x{e.GetComponent<Serial>().Value:X8}: replayed {replayedLen} fresh {_count - freshStart}, lights {replayLights - lightStart}/{_lightCount - replayLights}, frameinfo {fx},{fy},{fw},{fh} vs {_fiX},{_fiY},{_fiW},{_fiH}; body {s.Body} group {s.Group} dir {s.Dir} frame {s.Frame}");
                }
            }

            _count = savedCount;
            _viewArr = savedView;
            _viewCount = savedViewCount;
            _lightCount = replayLights;
            _fiX = fx;
            _fiY = fy;
            _fiW = fw;
            _fiH = fh;
        }
#endif
    }
}
