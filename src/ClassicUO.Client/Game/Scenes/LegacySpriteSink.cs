// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// T7b part 2a (setting <c>legacy_ecs_sprites</c>): the Legacy world's batcher sink for the dynamic-sprite extract. At the
    /// render list's view.Draw position (painter order unchanged) a mobile or world item is drawn from the ECS: the mobile
    /// through MobileAnimationSystem + <see cref="MobileSpriteExtract.Select"/>, the item through
    /// <see cref="ItemSpriteInput.Gather"/> + <see cref="ItemSpriteExtract.Select"/>, each record through
    /// <see cref="LegacySpriteResolve"/>. What the views did besides drawing comes from the extract's side outputs, as on the
    /// Gpu path (GpuWorldRenderer.TryDrawMobileEcs/EmitItem): the mobile's FrameInfo and mirror bit, the corpse's mirror bit,
    /// the item art entry's picking offset (written by the select), the aura, and the lights. While the switch is on, picking
    /// reads the same pick state from <see cref="SpritePickTable"/> (2b), which the sink records from the select. Effects stay
    /// on their views (no effect extract yet). A mobile without a mirror entity falls back to its view. CUO_LEGACY_ECS_PARITY=1 (<see cref="LegacyEcsParity"/>) compares the view's batcher calls with the sink's.
    /// </summary>
    internal static class LegacySpriteSink
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_LEGACY_ECS_SPRITES=1|0)
        private static readonly bool? _env = ParseSwitch(Environment.GetEnvironmentVariable("CUO_LEGACY_ECS_SPRITES"));
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>DevBench (pickscan's legacy_ecs_sprites mode) and tests: wins over the env override and the setting.</summary>
        internal static bool? Override;

        private static bool _on, _route;
        private static GameScene _scene;
        private static readonly ItemSpriteExtract _items = new ItemSpriteExtract();

        internal static bool? ParseSwitch(string value) => value switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        /// <summary>The switch: <see cref="Override"/>, else the env override, else the setting.</summary>
        internal static bool Resolve(bool? over, bool? env, bool setting) => over ?? env ?? setting;

        public static bool On => _on;

        /// <summary>This frame's mobiles and items go through <see cref="Draw"/> (the switch, or a parity frame).</summary>
        public static bool Routes => _route;

        /// <summary>Start of the Legacy world's render-list draw: reads the switch once for the frame.</summary>
        public static void BeginFrame()
        {
            _on = Resolve(Override, _env, Configuration.Settings.GlobalSettings.LegacyEcsSprites);
#if CUO_DEVTOOLS
            LegacyEcsParity.BeginFrame();
            _route = _on || LegacyEcsParity.Active;
#else
            _route = _on;
#endif
            _scene = _route ? Client.Game.GetScene<GameScene>() : null;
            SpritePickTable.BeginDraw();
        }

        /// <summary>End of the Legacy world's render-list draw (the render lists are also drawn by UI controls).</summary>
        public static void EndFrame()
        {
            _route = false;
            _scene = null;
            SpritePickTable.Commit();
        }

        /// <summary>The render list's draw of one object: the sink for mobiles and items while the switch (or the parity
        /// checker) is on, the view otherwise.</summary>
        public static bool Draw(UltimaBatcher2D batcher, GameObject obj, float depth)
        {
            if (!_route || obj is not (Mobile or Item))
            {
                return obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
            }

#if CUO_DEVTOOLS
            if (LegacyEcsParity.Active)
            {
                return LegacyEcsParity.Compare(batcher, obj, depth, _on, _scene);
            }
#endif

            return DrawEcs(batcher, obj, depth, true, out _);
        }

        /// <summary>
        /// The sink's draw of <paramref name="obj"/>. <paramref name="effects"/>: also apply the view's side effects (lights,
        /// FrameInfo, mirror bits); the parity checker passes false after the view has applied its own.
        /// <paramref name="ecs"/>: false = the object took the view path (a mobile without a mirror entity).
        /// </summary>
        internal static bool DrawEcs(UltimaBatcher2D batcher, GameObject obj, float depth, bool effects, out bool ecs)
        {
            ecs = true;

            if (obj is Mobile mobile)
            {
                if (!TryMobileEntity(mobile, out Friflo.Engine.ECS.Entity e))
                {
                    ecs = false;

                    if (mobile.IsDestroyed || !mobile.AllowedToDraw)
                    {
                        return false;
                    }

                    return mobile.Draw(batcher, mobile.RealScreenPosition.X, mobile.RealScreenPosition.Y, depth);
                }

                return DrawMobile(batcher, mobile, e, depth, effects);
            }

            return DrawItem(batcher, (Item)obj, depth, effects);
        }

        // E5 (shared with GpuWorldRenderer.TryDrawMobileEcs): false = not mirrored with an AnimationState, or not drawn
        private static bool TryMobileEntity(Mobile mobile, out Friflo.Engine.ECS.Entity e)
        {
            if (mobile.IsDestroyed || !mobile.AllowedToDraw)
            {
                e = default;

                return false;
            }

            return MobileEntityLookup.TryGet(mobile, out e);
        }

        private static bool DrawMobile(UltimaBatcher2D batcher, Mobile mobile, Friflo.Engine.ECS.Entity e, float depth, bool effects)
        {
            World world = mobile.World;
            WorldEcs ecs = world.Ecs;
            int posX = mobile.RealScreenPosition.X, posY = mobile.RealScreenPosition.Y;

            // as GpuWorldRenderer.TryDrawMobileEcs: TakeDirty first (removing the tag moves the entity)
            bool dirty = MobileAnimationSystem.TakeDirty(e);
            // one record read for both components (as ItemSpriteInput.ReadRecord)
            Friflo.Engine.ECS.EntityData data = e.Data;
            ref AnimationState state = ref data.Get<AnimationState>();
            ref MobileRenderState render = ref data.Get<MobileRenderState>();
            ecs.Animation.UpdateInPlace(world, mobile, e, dirty, ref state, ref render);

            mobile.DrawAura(batcher, posX, posY, depth);
            MobileSpriteExtract extract = ecs.MobileExtract;
            extract.BeginSelect(ClientLegacySpriteSource.Instance);
            extract.Select(e, posX, posY, depth);
            LegacySpriteResolve.DrawAll(batcher, ClientLegacySpriteSource.Instance, extract.Selected);
            bool mirror = (state.Flags & AnimStateFlags.Mirror) != 0;

            if (_on)
            {
                // picking reads these (the views' FrameInfo and mirror bit below stay: other readers, the mirror history)
                SpritePickTable.RecordMobile(mobile, extract.FrameInfo, mirror);
            }

            if (effects)
            {
                // the view's side effects: picking, text and health bars read FrameInfo and the mirror bit
                mobile.FrameInfo = extract.FrameInfo;
                mobile.ViewFlipped = mirror;
                AddMobileLights(mobile, extract);
            }

            return true;
        }

        internal static void AddMobileLights(Mobile mobile, MobileSpriteExtract extract)
        {
            if (_scene == null)
            {
                return;
            }

            foreach (ref readonly MobileSpriteExtract.LightRequest light in extract.Lights)
            {
                Item item = mobile.World.Items.Get(light.Item);

                if (item != null)
                {
                    _scene.AddLight(mobile, item, light.X, light.Y);
                }
            }
        }

        private static bool DrawItem(UltimaBatcher2D batcher, Item item, float depth, bool effects)
        {
            ItemSpriteInput.Gather(item, out ItemSpriteInput input);
            bool hasAnims = Client.Game.UO.FileManager.Animations.PackFrames != null;
            bool drawn = _items.Select(in input, hasAnims, item.RealScreenPosition.X, item.RealScreenPosition.Y, depth);

            if (_on)
            {
                SpritePickTable.RecordItem(item, _items);
            }

            if (effects)
            {
                if (_items.SetsFlipped)
                {
                    item.ViewFlipped = _items.Flipped;
                }

                if (_items.Light)
                {
                    _scene?.AddLight(item, item, _items.LightX, _items.LightY);
                }
            }

            LegacySpriteResolve.DrawAll(batcher, ClientLegacySpriteSource.Instance, _items.Selected);

            return drawn;
        }

        internal static ItemSpriteExtract ItemExtract => _items;
    }
}
