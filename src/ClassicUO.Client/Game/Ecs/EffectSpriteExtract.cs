// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;
using Uo.Render;

namespace ClassicUO.Game
{
    /// <summary>
    /// T7b part 3: one effect sprite as the select produces it: the art record (<see cref="DynamicSprite"/>, kind Art, rank 6,
    /// <see cref="DynamicSprite.Id"/> the effect's animation graphic as drawn: effects take no animated-art offset) plus what
    /// only effects have: the rotation, the blend mode, and which of the two effect draws it is.
    /// </summary>
    internal struct EffectSprite
    {
        public DynamicSprite Sprite;

        /// <summary>GameEffectView's angle to the target (radians); 0 for a drag effect.</summary>
        public float Rotation;

        public GraphicEffectBlendMode Blend;

        /// <summary>GameEffectView.Draw: View.DrawStaticRotated's rectangle draw, inside the blend state of
        /// <see cref="Blend"/>. False: DragEffect.Draw's View.DrawStatic (a plain position draw, no blend state set; the Gpu
        /// world still gives it the effect blend, as the capture does for every GameEffect).</summary>
        public bool Rotated;
    }

    /// <summary>
    /// T7b part 3: what the effect select reads. The effect's own state comes from its <see cref="EffectState"/> (graphic,
    /// animation graphic, hue, blend, position, offset, angle, kind); the scene's from the profile and the player; the draw
    /// permission from the object (IsDestroyed, AllowedToDraw, as the item input); the source object only for the depth and
    /// the light (GameEffectView's <c>SourceObject</c>: the object World holds for the live source).
    /// </summary>
    internal struct EffectSpriteInput
    {
        public byte Kind;
        public GraphicEffectBlendMode Blend;
        public ushort Graphic, AnimGraphic, Hue;

        /// <summary>The effect's tile x (the Gpu depth key's tie break).</summary>
        public ushort X;

        public float OffsetX, OffsetY, OffsetZ, Angle;

        /// <summary>GameEffectView: not destroyed and allowed to draw; DragEffect: not destroyed.</summary>
        public bool Drawable;

        /// <summary>The tiledata flags of <see cref="Graphic"/> (not the animation graphic, as the views read them).</summary>
        public bool TilePartialHue, TileTranslucent, TileLight;

        /// <summary>The views' hue choice: NoColorObjectsOutOfRange and farther than the view range; else the dead player's
        /// gray.</summary>
        public bool OutOfRange, DeadGray;

        /// <summary>The live source object (null: a ground point or a gone source); its depth + 1 replaces the draw depth of a
        /// GameEffectView draw.</summary>
        public GameObject Source;

        public float SourceDepth;

        /// <summary>The effect's state and the scene into <paramref name="i"/>. False: the effect has no state (ecs_effects
        /// off, or destroyed): the caller draws its view.</summary>
        public static bool Gather(GameEffect e, out EffectSpriteInput i)
        {
            i = default;

            if (e.EcsEntity.IsNull || e.IsDestroyed)
            {
                return false;
            }

            ref readonly EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            i.Kind = s.Kind;
            i.Blend = (GraphicEffectBlendMode)s.Blend;
            i.Graphic = s.Graphic;
            i.AnimGraphic = s.AnimGraphic;
            i.Hue = s.Hue;
            i.X = s.X;
            i.OffsetX = s.OffsetX;
            i.OffsetY = s.OffsetY;
            i.OffsetZ = s.OffsetZ;
            i.Angle = s.Angle;
            i.Drawable = s.Kind == EffectState.KIND_DRAG || e.AllowedToDraw;

            TileDataLoader tiles = Client.Game?.UO?.FileManager?.TileData;

            if (tiles != null && s.Graphic < tiles.StaticData.Length)
            {
                ref StaticTiles data = ref tiles.StaticData[s.Graphic];
                i.TilePartialHue = data.IsPartialHue;
                i.TileTranslucent = data.IsTranslucent;
                i.TileLight = data.IsLight;
            }

            World world = e.World;
            Profile profile = ProfileManager.CurrentProfile;

            if (profile != null)
            {
                // GameObject.Distance from the state's position (an effect is never the player and has no steps)
                int distance = world.Player == null
                    ? ushort.MaxValue
                    : Math.Max(Math.Abs(s.X - world.RangeSize.X), Math.Abs(s.Y - world.RangeSize.Y));

                i.OutOfRange = profile.NoColorObjectsOutOfRange && distance > world.ClientViewRange;
                i.DeadGray = !i.OutOfRange && world.Player != null && world.Player.IsDead && profile.EnableBlackWhiteEffect;
            }

            // GameEffectView reads the source for its depth always, DragEffect only for a light
            if (s.Kind != EffectState.KIND_DRAG || i.TileLight)
            {
                i.Source = e.SourceObject;

                if (i.Source != null && s.Kind != EffectState.KIND_DRAG)
                {
                    i.SourceDepth = i.Source.CalculateDepthZ() + 1f;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// T7b part 3 (setting <c>ecs_effect_sprites</c>): the renderer-neutral select of an effect's sprite, from
    /// <see cref="EffectSpriteInput"/> and the pack's art table, reproducing GameEffectView.Draw (fixed, moving and explosion
    /// effects: the rotated art draw of the animation graphic, the effect hue, the source's depth, the blend mode, the light
    /// at the source) and DragEffect.Draw. Lightning stays on its view (a gump drawn additively: the FNA overlay). Side effects
    /// kept: the art entry's draw offset (ArtEntry.Width/Height; art entries are shared with items and statics, whose picking
    /// reads them) and the light request (<see cref="Light"/>; the caller adds it for <see cref="EffectSpriteInput.Source"/>).
    /// Effects are not pickable (GameEffect.CheckMouseSelection), so nothing goes to SpritePickTable.
    /// The resolves: <see cref="GpuEffectResolve"/> (SpriteInstance, as GpuWorldRenderer's capture of the view) and
    /// <see cref="LegacySpriteResolve.ResolveEffect"/> (the view's batcher call).
    /// </summary>
    internal sealed class EffectSpriteExtract
    {
        private const int RANK_ITEM = 6;

        private readonly ArtLoader _artsOverride;
        private EffectSprite _sprite;

        public EffectSpriteExtract() { }

        /// <summary>Tests: the art loader the select reads instead of the client's.</summary>
        internal EffectSpriteExtract(ArtLoader arts)
        {
            _artsOverride = arts;
        }

        private ArtLoader Arts => _artsOverride ?? Client.Game.UO.FileManager.Arts;

        /// <summary>The last select produced a sprite (<see cref="Sprite"/>).</summary>
        public bool HasSprite { get; private set; }

        public ref readonly EffectSprite Sprite => ref _sprite;

        /// <summary>The art the effect draws has no pixels in the pack: the view draws Arts.GetArt's placeholder, so the caller
        /// draws the view instead (nothing else of the select applies).</summary>
        public bool MissingArt { get; private set; }

        /// <summary>The view would add a light at (LightX, LightY) for the source (GameScene.AddLight(source, source, …)).</summary>
        public bool Light { get; private set; }

        public int LightX { get; private set; }

        public int LightY { get; private set; }

        /// <summary>The effect kinds the extract reproduces: every one but lightning (a gump, drawn by its view).</summary>
        public static bool Handles(GameEffect e) => e is not LightningEffect;

        /// <summary>The view's Draw for one effect at screen position (<paramref name="posX"/>, <paramref name="posY"/>) with the
        /// render list's <paramref name="depth"/>. False = not drawn (the view's result).</summary>
        public bool Select(in EffectSpriteInput i, int posX, int posY, float depth)
        {
            HasSprite = false;
            MissingArt = false;
            Light = false;

            if (!i.Drawable || i.Kind == EffectState.KIND_LIGHTNING)
            {
                return false;
            }

            ushort hue = i.OutOfRange ? (ushort)Constants.OUT_RANGE_COLOR : i.DeadGray ? (ushort)Constants.DEAD_RANGE_COLOR : i.Hue;

            if (i.Kind == EffectState.KIND_DRAG)
            {
                // DragEffect.Draw: View.DrawStatic at the offset position, plain hue, no source depth
                Art(i.AnimGraphic, posX - ((int)i.OffsetX + 22), posY - ((int)-i.OffsetY + 22), ShaderHueTranslator.GetHueVector(hue), depth, i.X,
                    0f, i.Blend, false);

                if (!MissingArt && i.TileLight && i.Source != null)
                {
                    Light = true;
                    LightX = posX + 22;
                    LightY = posY + 22;
                }

                return true;
            }

            if (i.AnimGraphic == 0xFFFF)
            {
                return false;
            }

            posX += (int)i.OffsetX;
            posY += (int)(i.OffsetZ + i.OffsetY);
            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, i.TilePartialHue, i.TileTranslucent ? .5f : 1f, effect: true);

            if (i.Source != null)
            {
                depth = i.SourceDepth;
            }

            Art(i.AnimGraphic, posX, posY, hueVec, depth, i.X, i.Angle, i.Blend, true);

            if (!MissingArt && i.TileLight && i.Source != null)
            {
                Light = true;
                LightX = posX + 22;
                LightY = posY + 22;
            }

            return true;
        }

        // View.DrawStaticRotated / View.DrawStatic: the art graphic (no animated-art offset) at (x, y) less the art entry's
        // draw offset, which the view stores on the entry
        private void Art(ushort graphic, int x, int y, Vector3 hue, float depth, ushort objX, float rotation, GraphicEffectBlendMode blend, bool rotated)
        {
            ArtLoader arts = Arts;
            uint idx = (uint)graphic + 0x4000;
            SpriteEntry[] sprites = arts.PackSpriteEntries;

            if (sprites == null || idx >= sprites.Length || sprites[idx].OrigW == 0 || sprites[idx].OrigH == 0)
            {
                MissingArt = true;

                return;
            }

            ref readonly SpriteEntry sp = ref sprites[idx];
            int ow = sp.OrigW, oh = sp.OrigH;
            ref ArtEntry index = ref arts.GetEntry(graphic + 0x4000);
            index.Width = (short)((ow >> 1) - 22);
            index.Height = (short)(oh - 44);

            _sprite = new EffectSprite
            {
                Sprite = new DynamicSprite
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
                },
                Rotation = rotation,
                Blend = blend,
                Rotated = rotated
            };

            HasSprite = true;
        }
    }

    /// <summary>T7b part 3: the Gpu half of the effect extract: an <see cref="EffectSprite"/> to the <see cref="SpriteInstance"/>
    /// GpuWorldRenderer.Emit produces for the view's draw (art rect, trimmed; rotated about the untrimmed top-left; the blend
    /// as GpuWorldRenderer.BlendOf maps it).</summary>
    internal static class GpuEffectResolve
    {
        public static void Resolve(in GpuSpriteTables t, in EffectSprite e, int offsetX, int offsetY, ref SpriteInstance[] buffer, ref int count)
        {
            ref readonly DynamicSprite s = ref e.Sprite;
            uint idx = s.Id + 0x4000;

            if (t.Sprites == null || idx >= t.Sprites.Length)
            {
                return;
            }

            ref readonly SpriteEntry sp = ref t.Sprites[idx];
            Vector3 hue = s.Hue;
            bool cot = hue.Z > 1f;
            float alpha = cot ? hue.Z - 1f : hue.Z;

            if (alpha <= 0f || !sp.Present)
            {
                return;
            }

            if (!GpuSpriteResolve.HueBits(in t, hue, out uint flags, out uint hueRow, out HueMode hueMode))
            {
                return;
            }

            // the art rect is the whole untrimmed sprite: GpuWorldRenderer.Emit's clip to the trimmed rect
            int ix0 = Math.Max(0, (int)sp.TrimX), iy0 = Math.Max(0, (int)sp.TrimY);
            int ix1 = Math.Min(sp.OrigW, sp.TrimX + sp.W), iy1 = Math.Min(sp.OrigH, sp.TrimY + sp.H);

            if (ix1 <= ix0 || iy1 <= iy0)
            {
                return;
            }

            int px = s.X + offsetX, py = s.Y + offsetY;
            int dx = ix0, dy = iy0;
            float rotation = e.Rotation;
            int x, y;

            if (rotation != 0f)
            {
                float sn = MathF.Sin(rotation), cs = MathF.Cos(rotation);
                x = px + (int)MathF.Round(cs * dx - sn * dy);
                y = py + (int)MathF.Round(sn * dx + cs * dy);
            }
            else
            {
                x = px + dx;
                y = py + dy;
            }

            if (count >= buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            ref SpriteInstance inst = ref buffer[count++];
            inst.X = x;
            inst.Y = y;
            inst.Whf = SpriteInstance.PackWhf(ix1 - ix0, iy1 - iy0, flags);
            inst.Uv = (uint)(sp.X + (ix0 - sp.TrimX)) | ((uint)(sp.Y + (iy0 - sp.TrimY)) << 16);
            inst.PageHue = (uint)sp.Page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(s.Depth * 100f) - 50, s.ObjX, s.Rank);
            inst.Light = 0;
            inst.Corners = 0;
            inst.Mode = hueMode;

            if (cot)
            {
                inst.SetLightBits(SpriteInstance.LightCot);
            }

            InstanceBlend blend = GpuWorldRenderer.BlendOf(e.Blend);

            if (blend != InstanceBlend.Normal)
            {
                inst.Blend = blend;
            }

            if (rotation != 0f)
            {
                inst.SetRotation(rotation);
            }

            if (alpha < 1f)
            {
                inst.Alpha = (byte)Math.Clamp((int)(alpha * 255f + 0.5f), 1, 254);
            }
        }
    }
}
