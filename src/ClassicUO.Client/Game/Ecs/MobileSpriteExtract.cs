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
    /// Screen position and base depth are inputs (W2's ScreenPos); not yet the renderer's source (see EcsParity).
    /// </summary>
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
            _fiX = _fiY = _fiW = _fiH = 0;
            ushort bodyHue = data.Get<Hue>().Value;

            int drawX = posX + rs.OffsetX + 22;
            int drawY = posY - 3 + rs.OffsetY + 22;
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

            for (int i = 0; i < eq.LightCount; i++)
            {
                AddLight(eq.LightItems[i], drawX, drawY);
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

            x -= mirror ? w - fe.CenterX : fe.CenterX;
            y -= h + fe.CenterY;

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
                    Write(index, x, y + strips[i].Y, strips[i].Y, strips[i].H, hueVec, mirror, depth, 1, i * 2);
                }
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
                    _record.EnsureCapacity(i + 1);
                    ref MobileFrameCache.Entry entry = ref _record.Entries[i];
                    entry.Kind = kind;
                    entry.Add = add;
                }
            }
        }

        // the seated body's waist and knee rows for its equipment (MobileView's statics), and whether the mobile is human
        private SitDeform.BodyRows _sitRows;
        private bool _human;
        private ushort _selBody;
        private byte _selGroup, _selDir, _selFrame;
        private bool _selEquip;

        internal static float DepthOf(float baseDepth, byte kind, float add) => kind == 0 ? baseDepth : baseDepth + 1f + add;

        private MobileFrameCache _record;
        private int _recordStart;

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
                cache.BodyHue == bodyHue && ReferenceEquals(cache.Pack, _pack))
            {
                MobileFrameCache.EmitHits++;
                // a pending view from an earlier emit since Begin aliases cache.Abs (the same cache replayed twice): move it
                // into _out before Replay mutates Abs, and before `start` is taken
                MaterializeView();
                int objX = data.Get<Position>().X;
                int lightStart = _lightCount;
                int start = _count;
                Replay(cache, baseX + _offsetX, baseY + _offsetY, depth, objX, baseX, baseY);

                if (MobileAnimationSystem.CacheCheck)
                {
                    CheckReplay(e, posX, posY, depth, start, lightStart);
                }

                return;
            }

            MaterializeView();
            MobileFrameCache.EmitMisses++;
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
            cache.EnsureCapacity(n);

            for (int i = 0; i < n; i++)
            {
                SpriteInstance inst = _out[recStart + i];
                inst.X -= baseX + _offsetX;
                inst.Y -= baseY + _offsetY;
                cache.Entries[i].Instance = inst;
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
        internal void Replay(MobileFrameCache cache, int dx, int dy, float depth, int objX, int baseX, int baseY)
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

                RebuildAbs(cache, abs, n, dx, dy, depth, objX);
            }
            else if (cache.AbsDepth != depth || cache.AbsObjX != objX)
            {
                RebuildAbs(cache, abs, n, dx, dy, depth, objX);
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

        private static void RebuildAbs(MobileFrameCache cache, SpriteInstance[] abs, int n, int dx, int dy, float depth, int objX)
        {
            for (int i = 0; i < n; i++)
            {
                ref readonly MobileFrameCache.Entry entry = ref cache.Entries[i];
                ref SpriteInstance inst = ref abs[i];
                inst = entry.Instance;
                inst.X += dx;
                inst.Y += dy;
                inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(DepthOf(depth, entry.Kind, entry.Add) * 100f) - 50, objX, RANK_MOBILE);
            }

            cache.AbsValid = true;
            cache.AbsDx = dx;
            cache.AbsDy = dy;
            cache.AbsDepth = depth;
            cache.AbsObjX = objX;
        }

        /// <summary>
        /// legacy_ecs_sprites: the Legacy batcher calls of <see cref="Select(EcsEntity, in EcsRow, int, int, float)"/> resolved
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
                cache.LegacyOverrideHue == rs.OverrideHue && cache.LegacyBodyHue == bodyHue && ReferenceEquals(cache.LegacySource, src))
            {
                MobileFrameCache.LegacyHits++;
                _selCount = 0;


                MobileFrameCache.LegacyEntry[] entries = cache.LegacyEntries;

                for (int i = 0; i < cache.LegacyCount; i++)
                {
                    // LegacySpriteResolve.Draw of LegacyReplayed's call, from the entry's fields (recorded entries are
                    // normal or shadow draws, never seated or rotated)
                    ref readonly MobileFrameCache.LegacyEntry entry = ref entries[i];
                    var position = new Vector2(baseX + entry.X, baseY + entry.Y);
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

                if (MobileAnimationSystem.CacheCheck)
                {
                    CheckLegacyReplay(src, e, in data, cache, posX, posY, depth, baseX, baseY);
                }

                return;
            }

            MobileFrameCache.LegacyMisses++;
            Select(e, data, posX, posY, depth);

            // a seated mobile's equipment reads the body rows the last seated human body left (MobileView's statics): not replayed
            bool record = cache.SelectValid && !_atlasMiss && (s.Flags & AnimStateFlags.CharSitting) == 0;

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
            cache.LegacyAlpha = rs.Alpha;
            cache.LegacyOverrideHue = rs.OverrideHue;
            cache.LegacyBodyHue = bodyHue;
            cache.LegacySource = src;
            cache.LegacyValid = true;
        }

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
            SpriteInstance[] replayed = _viewArr != null
                ? new ReadOnlySpan<SpriteInstance>(_viewArr, 0, _viewCount).ToArray()
                : new ReadOnlySpan<SpriteInstance>(_out, start, _count - start).ToArray();
            SpriteInstance[] savedView = _viewArr;
            int savedViewCount = _viewCount, savedCount = _count;
            int replayLights = _lightCount;
            int fx = _fiX, fy = _fiY, fw = _fiW, fh = _fiH;

            _viewArr = null;
            Emit(e, posX, posY, depth);
            CheckReplays++;

            int freshStart = savedCount;
            bool same = _count - freshStart == replayed.Length && _lightCount - replayLights == replayLights - lightStart &&
                        fx == _fiX && fy == _fiY && fw == _fiW && fh == _fiH;

            for (int i = 0; same && i < replayed.Length; i++)
            {
                same = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<SpriteInstance>(replayed, i, 1))
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
                    ClassicUO.Utility.Logging.Log.Warn($"[mobile-cache-check] instances differ 0x{e.GetComponent<Serial>().Value:X8}: replayed {replayed.Length} fresh {_count - freshStart}, lights {replayLights - lightStart}/{_lightCount - replayLights}, frameinfo {fx},{fy},{fw},{fh} vs {_fiX},{_fiY},{_fiW},{_fiH}; body {s.Body} group {s.Group} dir {s.Dir} frame {s.Frame}");
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
    }
}
