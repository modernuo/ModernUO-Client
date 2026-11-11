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
    /// T7b part 2a: the Legacy world's batcher sink for the dynamic-sprite extract. At the
    /// render list's view.Draw position (painter order unchanged) a mobile or world item is drawn from the ECS: the mobile
    /// through MobileAnimationSystem + <see cref="MobileSpriteExtract.Select"/>, the item through
    /// <see cref="ItemSpriteInput.Gather"/> + <see cref="ItemSpriteExtract.Select"/>, each record through
    /// <see cref="LegacySpriteResolve"/>. What the views did besides drawing comes from the extract's side outputs, as on the
    /// Gpu path (GpuWorldRenderer.TryDrawMobileEcs/EmitItem): the mobile's FrameInfo and mirror bit, the corpse's mirror bit,
    /// the item art entry's picking offset (written by the select), the aura, and the lights. Picking reads the same pick
    /// state from <see cref="SpritePickTable"/> (2b), which the sink records from the select. Effects have their own sink
    /// (EffectSpriteSink). A mobile without a mirror entity falls back to its view (MobileView.Draw), as do objects that are
    /// neither mobiles nor items. T9 deleted the switch <c>legacy_ecs_sprites</c> (its off path drew every mobile and item
    /// through its view) and the checker CUO_LEGACY_ECS_PARITY.
    /// </summary>
    internal static class LegacySpriteSink
    {
        private static bool _hasAnims;
        private static GameScene _scene;
        private static readonly ItemSpriteExtract _items = new ItemSpriteExtract();

        /// <summary>Start of the Legacy world's render-list draw.</summary>
        public static void BeginFrame()
        {
            _scene = Client.Game.GetScene<GameScene>();
            // the pack's animation tables are loaded once at startup: read per frame, not per item
            _hasAnims = HasPackAnims(Client.Game.UO.FileManager.Animations);
            ItemSpriteInput.BeginFrame();
            SpritePickTable.BeginDraw();
        }

        /// <summary>Whether corpses can be drawn from the pack's animations (DrawItem's select input).</summary>
        internal static bool HasPackAnims(AnimationsLoader anims) => anims.PackFrames != null;

        /// <summary>End of the Legacy world's render-list draw (the render lists are also drawn by UI controls).</summary>
        public static void EndFrame()
        {
            _scene = null;
            SpritePickTable.Commit();
        }

        /// <summary>The render list's draw of one object: the sink for mobiles and items, the view for the rest.</summary>
        public static bool Draw(UltimaBatcher2D batcher, GameObject obj, float depth)
        {
            // items first: most of the list, and Item is sealed (one type compare)
            if (obj is Item)
            {
                return DrawItem(batcher, Unsafe.As<Item>(obj), depth);
            }

            if (obj is Mobile)
            {
                return DrawMobileOrView(batcher, Unsafe.As<Mobile>(obj), depth);
            }

            return obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
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
        /// legacy-sprites-fix: called by the render-list loop before it draws entry <paramref name="i"/>.
        /// Three prefetch stages, each reading only what the stage before fetched: the object <see cref="OBJECT_AHEAD"/>
        /// entries ahead (its header and its mirror handle, from the list's reference alone), the mirror node of the item
        /// <see cref="NODE_AHEAD"/> ahead, and the record components
        /// <see cref="ItemSpriteInput.ReadRecord(Item, Assets.StaticTiles[], ref ItemSpriteInput, out ushort)"/> reads of the
        /// item <see cref="ROW_AHEAD"/> ahead. The record is in the ECS's per-component arrays, one cache line each, which the
        /// item draw otherwise waits on (the large scene's ~25k items per frame). Prefetches only: a stale handle costs a
        /// wasted fetch, never a wrong read. (A tiledata stage was tried and cost more than it saved: bench-000527.)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Prefetch(ReadOnlySpan<GameObject> list, int i) => Prefetch(list, i, _ahead);

        /// <summary><see cref="Prefetch(ReadOnlySpan{GameObject}, int)"/> with explicit distances (tests).</summary>
        internal static void Prefetch(ReadOnlySpan<GameObject> list, int i, (int Obj, int Node, int Row) ahead)
        {
            (int obj, int node, int row) = ahead;
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
                // the one packed row Gather reads
                EcsRead.PrefetchRow<ClassicUO.Ecs.ItemDrawRow>(near.EcsEntity);
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

        private static bool DrawMobileOrView(UltimaBatcher2D batcher, Mobile mobile, float depth)
        {
            if (!TryMobileEntity(mobile, out Friflo.Engine.ECS.Entity e))
            {
                if (mobile.IsDestroyed || !mobile.AllowedToDraw)
                {
                    return false;
                }

                return mobile.Draw(batcher, mobile.RealScreenPosition.X, mobile.RealScreenPosition.Y, depth);
            }

            return DrawMobile(batcher, mobile, e, depth);
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

        private static bool DrawMobile(UltimaBatcher2D batcher, Mobile mobile, Friflo.Engine.ECS.Entity e, float depth)
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
            // picking reads these (the views' FrameInfo and mirror bit below stay: other readers, the mirror history)
            SpritePickTable.RecordMobile(mobile, extract.FrameInfo, mirror);
            // the view's side effects: picking, text and health bars read FrameInfo and the mirror bit
            mobile.FrameInfo = extract.FrameInfo;
            mobile.ViewFlipped = mirror;
            AddMobileLights(mobile, extract);

            return true;
        }

        private static void AddMobileLights(Mobile mobile, MobileSpriteExtract extract)
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

        private static bool DrawItem(UltimaBatcher2D batcher, Item item, float depth)
        {
            ItemSpriteInput.Gather(item, out ItemSpriteInput input);
            // the records are drawn as the select makes them (the batcher calls of a select + DrawAll); the side effects below
            // do not touch the batcher
            bool drawn = _items.SelectDraw(batcher, ClientLegacySpriteSource.Instance, in input, _hasAnims, item.RealScreenPosition.X,
                item.RealScreenPosition.Y, depth);

            SpritePickTable.RecordItem(item, _items);

            if (_items.SetsFlipped)
            {
                item.ViewFlipped = _items.Flipped;
            }

            if (_items.Light)
            {
                _scene?.AddLight(item, item, _items.LightX, _items.LightY);
            }

            return drawn;
        }
    }
}
