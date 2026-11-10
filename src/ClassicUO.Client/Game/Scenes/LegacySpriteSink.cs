// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
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
    /// reads the same pick state from <see cref="SpritePickTable"/> (2b), which the sink records from the select. Effects have their
    /// own sink (EffectSpriteSink, ecs_effect_sprites). A mobile without a mirror entity falls back to its view. CUO_LEGACY_ECS_PARITY=1 (<see cref="LegacyEcsParity"/>) compares the view's batcher calls with the sink's.
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

        private static bool _on, _route, _hasAnims, _drawRow, _cache;
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
            // the pack's animation tables are loaded once at startup: read per frame, not per item
            _hasAnims = _route && HasPackAnims(Client.Game.UO.FileManager.Animations);
            _drawRow = ItemDrawRows.On;
            _cache = _route && ItemDrawCache.On;
            ItemSpriteInput.BeginFrame();
            SpritePickTable.BeginDraw();
        }

        /// <summary>Whether corpses can be drawn from the pack's animations (DrawItem's select input).</summary>
        internal static bool HasPackAnims(AnimationsLoader anims) => anims.PackFrames != null;

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
            // items first: most of the list, and Item is sealed (one type compare)
            bool item = obj is Item;

            if (!_route || !item && obj is not Mobile)
            {
                return obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
            }

#if CUO_DEVTOOLS
            if (LegacyEcsParity.Active)
            {
                return LegacyEcsParity.Compare(batcher, obj, depth, _on, _scene);
            }
#endif

            return item ? DrawItem(batcher, Unsafe.As<Item>(obj), depth, true) : DrawMobileOrView(batcher, Unsafe.As<Mobile>(obj), depth, true, out _);
        }

        // how many render-list entries ahead DrawWorldRenderList prefetches an object's own memory, then an item's mirror node,
        // then its record
        private const int OBJECT_AHEAD = 16, NODE_AHEAD = 8, ROW_AHEAD = 4;

#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_LEGACY_PREFETCH=obj,node,row overrides the three distances for timing arms (0 = stage off)
        private static readonly (int Obj, int Node, int Row) _ahead = ParsePrefetch(Environment.GetEnvironmentVariable("CUO_LEGACY_PREFETCH"));
#else
        private static readonly (int Obj, int Node, int Row) _ahead = (OBJECT_AHEAD, NODE_AHEAD, ROW_AHEAD);
#endif

        /// <summary>The prefetch distances (object, node, row) of <c>CUO_LEGACY_PREFETCH=obj,node,row</c>; the defaults when
        /// it is unset or malformed. 0 turns a stage off.</summary>
        internal static (int Obj, int Node, int Row) ParsePrefetch(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                string[] parts = value.Split(',');

                if (parts.Length == 3 && int.TryParse(parts[0], out int obj) && int.TryParse(parts[1], out int node) &&
                    int.TryParse(parts[2], out int row) && obj >= 0 && node >= 0 && row >= 0 && obj <= 64 && node <= 64 && row <= 64)
                {
                    return (obj, node, row);
                }
            }

            return (OBJECT_AHEAD, NODE_AHEAD, ROW_AHEAD);
        }

        internal static (int Obj, int Node, int Row) PrefetchDistances => _ahead;

        // the byte offset of Entity.EcsEntity from an object's first field (the line the node stage reads); -1 until measured
        private static nint _handleOffset = -1;

        /// <summary>
        /// legacy-sprites-fix: called by the routed render-list loop (switch on) before it draws entry <paramref name="i"/>.
        /// Three prefetch stages, each reading only what the stage before fetched: the object <see cref="OBJECT_AHEAD"/>
        /// entries ahead (its header and its mirror handle, from the list's reference alone), the mirror node of the item
        /// <see cref="NODE_AHEAD"/> ahead, and the record components
        /// <see cref="ItemSpriteInput.ReadRecord(Item, Assets.StaticTiles[], ref ItemSpriteInput, out ushort)"/> reads of the
        /// item <see cref="ROW_AHEAD"/> ahead. The record is in the ECS's per-component arrays, one cache line each, which the
        /// item draw otherwise waits on (the large scene's ~25k items per frame). Prefetches only: a stale handle costs a
        /// wasted fetch, never a wrong read. (A tiledata stage was tried and cost more than it saved: bench-000527.)
        /// </summary>
        internal static void Prefetch(ReadOnlySpan<GameObject> list, int i)
        {
            (int obj, int node, int row) = _ahead;
            int j = i + obj;

            if (obj != 0 && (uint)j < (uint)list.Length)
            {
                PrefetchObject(list[j]);
            }

            j = i + node;

            if (node != 0 && (uint)j < (uint)list.Length && list[j] is Item far && far.EcsEntityHeld)
            {
                EcsRead.PrefetchNode(far.EcsEntity);
            }

            j = i + row;

            if (row != 0 && (uint)j < (uint)list.Length && list[j] is Item near && near.EcsEntityHeld)
            {
                if (_drawRow || _cache)
                {
                    // ecs_item_draw_row: the one packed row Gather reads; legacy_item_draw_cache: the row's version
                    EcsRead.PrefetchRow<ClassicUO.Ecs.ItemDrawRow>(near.EcsEntity);
                }
                else
                {
                    EcsRead.PrefetchRow<ClassicUO.Ecs.Graphic, ClassicUO.Ecs.Hue, ClassicUO.Ecs.Position, ClassicUO.Ecs.Flags>(near.EcsEntity);
                }
            }
        }

        // the object's header line (its type, which the node stage tests) and the line of its mirror handle, without reading
        // the object
        private static unsafe void PrefetchObject(GameObject obj)
        {
            if (!System.Runtime.Intrinsics.X86.Sse.IsSupported || obj == null)
            {
                return;
            }

            nint offset = _handleOffset;

            if (offset < 0 && obj is Entity entity)
            {
                offset = _handleOffset = HandleOffset(entity);
            }

            byte* p = (byte*)Unsafe.As<GameObject, nint>(ref obj);
            System.Runtime.Intrinsics.X86.Sse.Prefetch0(p);

            if (offset >= 0)
            {
                System.Runtime.Intrinsics.X86.Sse.Prefetch0(p + sizeof(nint) + offset);
            }
        }

        internal static nint HandleOffset(Entity entity) =>
            Unsafe.ByteOffset(ref Unsafe.As<RawObject>(entity).Data, ref Unsafe.As<Friflo.Engine.ECS.Entity, byte>(ref entity.EcsEntity));

        private sealed class RawObject
        {
            public byte Data;
        }

        /// <summary>
        /// The sink's draw of <paramref name="obj"/>. <paramref name="effects"/>: also apply the view's side effects (lights,
        /// FrameInfo, mirror bits); the parity checker passes false after the view has applied its own.
        /// <paramref name="ecs"/>: false = the object took the view path (a mobile without a mirror entity).
        /// </summary>
        internal static bool DrawEcs(UltimaBatcher2D batcher, GameObject obj, float depth, bool effects, out bool ecs)
        {
            if (obj is Item item)
            {
                ecs = true;

                return DrawItem(batcher, item, depth, effects);
            }

            return DrawMobileOrView(batcher, (Mobile)obj, depth, effects, out ecs);
        }

        private static bool DrawMobileOrView(UltimaBatcher2D batcher, Mobile mobile, float depth, bool effects, out bool ecs)
        {
            ecs = true;

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

            // one record read for both components (as ItemSpriteInput.ReadRecord); the dirty tag is read from it, and removing
            // the tag moves the entity, so the row is read again then
            EcsRow data = EcsRead.Row(e);
            bool dirty = MobileAnimationSystem.TakeDirty(e, in data);

            if (dirty)
            {
                data = EcsRead.Row(e);
            }

            ref AnimationState state = ref data.Get<AnimationState>();
            ref MobileRenderState render = ref data.Get<MobileRenderState>();
            ecs.Animation.UpdateInPlace(world, mobile, e, data, dirty, ref state, ref render);

            mobile.DrawAura(batcher, posX, posY, depth);
            MobileSpriteExtract extract = ecs.MobileExtract;
            // the select and resolve, or their calls replayed from the mobile's MobileFrameCache (as the Gpu path's Emit)
            extract.DrawLegacy(batcher, ClientLegacySpriteSource.Instance, e, data, in state, in render, posX, posY, depth, mobile.FrameCache);
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
                Item item = mobile.World.FindItem(light.Item);

                if (item != null)
                {
                    _scene.AddLight(mobile, item, light.X, light.Y);
                }
            }
        }

        private static bool DrawItem(UltimaBatcher2D batcher, Item item, float depth, bool effects)
        {
            ItemDrawCache cache = null;

            if (_cache)
            {
                cache = item.DrawCache;

                if (cache != null && ItemSpriteInput.CacheHit(item, cache, _items.ArtsLoader))
                {
                    ReplayItem(batcher, item, cache, depth, effects);

                    return true;
                }

                ItemDrawCache.Misses++;
            }

            ItemSpriteInput.Gather(item, out ItemSpriteInput input);
            int posX = item.RealScreenPosition.X, posY = item.RealScreenPosition.Y;
            bool drawn;

            if (_cache && !input.IsCorpse && !input.PlacementPreview && !input.Destroyed && input.AllowedToDraw &&
                ItemSpriteInput.DrawVersion(item) is var version and not 0)
            {
                // legacy_item_draw_cache: the same calls, recorded for the next frames (one cache per item, made once)
                cache ??= item.DrawCache = new ItemDrawCache();
                drawn = _items.SelectDraw(batcher, ClientLegacySpriteSource.Instance, in input, _hasAnims, posX, posY, depth, cache, version);
            }
            else
            {
                if (cache != null)
                {
                    cache.Valid = false;
                }

                // the records are drawn as the select makes them (the batcher calls of a select + DrawAll); the side effects
                // below do not touch the batcher
                drawn = _items.SelectDraw(batcher, ClientLegacySpriteSource.Instance, in input, _hasAnims, posX, posY, depth);
            }

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

            return drawn;
        }

        // legacy_item_draw_cache hit: DrawItem's batcher calls and side effects from the cache (an art item: no mirror bit)
        private static void ReplayItem(UltimaBatcher2D batcher, Item item, ItemDrawCache cache, float depth, bool effects)
        {
            ItemDrawCache.Hits++;
            int posX = item.RealScreenPosition.X, posY = item.RealScreenPosition.Y;
            cache.Replay(batcher, posX, posY, depth);

            if (_on && cache.HasArtPick)
            {
                SpritePickTable.RecordItem(item, false, false, true, cache.PickGraphic, cache.PickW, cache.PickH);
            }

            if (effects && cache.Light)
            {
                _scene?.AddLight(item, item, posX + 22, posY + 22);
            }

            if (ItemDrawCache.Check)
            {
                CheckReplay(item, cache, posX, posY, depth);
            }
        }

        // CUO_ITEM_DRAW_CACHE_CHECK=1: a fresh Gather + select of the replayed item, resolved, against the replayed calls and
        // side outputs
        private static void CheckReplay(Item item, ItemDrawCache cache, int posX, int posY, float depth)
        {
            ItemDrawCache.Checks++;
            ItemSpriteInput.Gather(item, out ItemSpriteInput input);
            _items.Select(in input, _hasAnims, posX, posY, depth);
            string problem = null;

            if (_items.Light != cache.Light || _items.Light && (_items.LightX != posX + 22 || _items.LightY != posY + 22))
            {
                problem = $" light {cache.Light} vs {_items.Light}";
            }

            if (_items.HasArtPick != cache.HasArtPick || cache.HasArtPick &&
                (_items.ArtPickGraphic != cache.PickGraphic || _items.ArtPickW != cache.PickW || _items.ArtPickH != cache.PickH))
            {
                problem += $" pick {cache.HasArtPick} 0x{cache.PickGraphic:X4} {cache.PickW},{cache.PickH} vs {_items.HasArtPick} 0x{_items.ArtPickGraphic:X4} {_items.ArtPickW},{_items.ArtPickH}";
            }

            if (_items.SetsFlipped)
            {
                problem += " fresh select sets the mirror bit";
            }

            ReadOnlySpan<DynamicSprite> fresh = _items.Selected;
            int k = 0;

            for (int i = 0; i < fresh.Length; i++)
            {
                if (!LegacySpriteResolve.Resolve(ClientLegacySpriteSource.Instance, in fresh[i], out LegacyDraw d))
                {
                    continue;
                }

                if (k < cache.Calls && problem == null)
                {
                    LegacyDraw replayed = cache.Call(k, posX, posY, depth);

                    if (!replayed.Same(in d))
                    {
                        problem = $" call {k}: replayed {replayed} | fresh {d}";
                    }
                }

                k++;
            }

            if (k != cache.Calls)
            {
                problem += $" calls {cache.Calls} vs {k}";
            }

            if (problem != null)
            {
                ItemDrawCache.Report(item, problem);
            }
        }

        internal static ItemSpriteExtract ItemExtract => _items;
    }
}
