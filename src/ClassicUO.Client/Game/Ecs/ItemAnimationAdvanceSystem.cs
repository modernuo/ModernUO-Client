// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>T5b-items: the animation data a corpse's frame advance reads.</summary>
    internal interface ICorpseAnimationSource
    {
        int MaxAnimationCount { get; }

        void GetAnimDirection(ref byte dir, ref bool mirror);

        /// <summary><c>AnimationsLoader.TryCorpseFrame</c>: the frame <paramref name="next"/> held at the last frame of the corpse's
        /// death action; false when that direction has no frames.</summary>
        bool TryCorpseFrame(ushort graphic, bool second, byte storedDir, byte next, out byte frame);
    }

    /// <summary><see cref="ICorpseAnimationSource"/> over the client's animation data.</summary>
    internal readonly struct LiveCorpseSource : ICorpseAnimationSource
    {
        public int MaxAnimationCount => Client.Game.UO.Animations.MaxAnimationCount;

        public void GetAnimDirection(ref byte dir, ref bool mirror) => Client.Game.UO.Animations.GetAnimDirection(ref dir, ref mirror);

        public bool TryCorpseFrame(ushort graphic, bool second, byte storedDir, byte next, out byte frame) =>
            Client.Game.UO.FileManager.Animations.TryCorpseFrame(graphic, second, storedDir, next, out frame);
    }

    /// <summary>
    /// T5b-items: <c>Item.ProcessAnimation</c> over the ECS state. The same fields as a mobile's <see cref="AnimationTimer"/>
    /// (Frame, ExecuteAnimation, AnimDue) but different rules, stated here because <c>AnimationTiming.Advance</c> is not
    /// reusable: a corpse has no interval, repeat count, idle animation, group selection, random draw or removal. One step is
    /// due when <c>AnimDue &lt; now</c> (the mobile's is <c>AnimDue &gt;= now</c> not due: the same test); the next frame is
    /// <c>Frame + 1</c> (when ExecuteAnimation, which stays true for items) held at the last frame of the death action by
    /// <c>TryCorpseFrame</c>, and the next due time is now + the character animation delay. The corpse's death action comes
    /// from its Corpse.def target (the graphic is the item's Amount), its direction from the item's Layer and the action's
    /// second variant from UsedLayer.
    /// </summary>
    internal static class CorpseAnimationRules
    {
        /// <summary>One <c>Item.ProcessAnimation</c> of a corpse: false when it is not due. <paramref name="frame"/> is -1 when the
        /// animation data has no frames (the frame index stays); <paramref name="due"/> is the next due time.</summary>
        public static bool Advance<TSource>(ref AnimationTimer t, in ItemData data, in TSource source, out int frame, out long due)
            where TSource : struct, ICorpseAnimationSource
        {
            frame = -1;
            due = 0;

            if (t.AnimDue >= Time.Ticks)
            {
                return false;
            }

            byte frameIndex = (byte)(t.Frame + (t.ExecuteAnimation ? 1 : 0));
            ushort id = data.Amount;
            byte dir = data.Layer;
            bool mirror = false;
            source.GetAnimDirection(ref dir, ref mirror);

            if (id < source.MaxAnimationCount && dir < 5 && source.TryCorpseFrame(id, t.UsedLayer, dir, frameIndex, out byte next))
            {
                frame = next;
            }

            due = Time.Ticks + Constants.CHARACTER_ANIMATION_DELAY;

            if (frame >= 0)
            {
                t.Frame = (byte)frame;
            }

            t.AnimDue = due;

            return true;
        }
    }

    /// <summary>
    /// T5b-items (<c>world_item_anim_system</c>): runs a corpse's <c>Item.Update</c> in the ECS when it is in the active item
    /// list: <see cref="CorpseAnimationRules.Advance{TSource}"/> over its <see cref="AnimationTimer"/> and <see cref="ItemData"/>,
    /// the results through the funnel (<c>WorldWriter.AdvanceCorpseFrame</c>, the same write sites as the legacy path). The
    /// active item stage visits items in the list's order and calls <c>Item.Update</c> for any this declines (handles that
    /// just opened, items that are not corpses), so shared side effects keep their sequence. No random draw happens in
    /// either path.
    /// </summary>
    internal sealed class ItemAnimationAdvanceSystem
    {
        // CUO_ANIM_ADVANCE_CHECK=1: each advance is compared with the legacy one on a shadow (see Check); the unit tests pass
        // the check flag themselves, so Check stays compiled in release
#if CUO_DEVTOOLS
        public static readonly bool AdvanceCheck = MobileAnimationAdvanceSystem.AdvanceCheck;
#else
        public const bool AdvanceCheck = false;
#endif

        public static long Ran, Declined, NotDue;
        public static long Checks, Diffs, SyncChecks, SyncDiffs, RandomDiffs;
        private static int _logged;

        public bool TryRun(World world, Item item) =>
            TryRun(world, item, new LiveCorpseSource(), AdvanceCheck, null);

        /// <summary>
        /// Advances <paramref name="item"/> in the ECS; false (nothing changed) when it is not eligible: destroyed, not a
        /// corpse, a handles opening (Entity.Update's work), not mirrored or without the components. The caller then runs
        /// <c>Item.Update</c>. <paramref name="legacy"/> (the checker's shadow) is the legacy computation, null in the client:
        /// it reads the client's animation data.
        /// </summary>
        internal bool TryRun<TSource>(World world, Item item, in TSource source, bool check, LegacyShadow legacy)
            where TSource : struct, ICorpseAnimationSource
        {
            if (item.IsDestroyed || !item.IsCorpse || item.ObjectHandlesStatus == ObjectHandlesStatus.OPEN ||
                !world.Ecs.Mirror.TryGetEntity(item.Serial, false, out EcsEntity e) || !e.HasComponent<AnimationTimer>() ||
                !e.HasComponent<ItemData>() || !ReferenceEquals(world.Items.Get(item.Serial), item))
            {
                Declined++;

                return false;
            }

            AnimationTimer t = EcsRead.Get<AnimationTimer>(e);
            ItemData data = EcsRead.Get<ItemData>(e);

            if (check)
            {
                Check(item, in t, in data, in source, legacy ?? LiveLegacy);
            }

            if (!CorpseAnimationRules.Advance(ref t, in data, in source, out int frame, out long due))
            {
                NotDue++;

                return true;
            }

            Ran++;
            EcsRead.Get<AnimationTimer>(e) = t;
            world.Writer.AdvanceCorpseFrame(item, frame, due, push: false);

            return true;
        }

        /// <summary>The legacy computation on a shadow: the frame and due time <c>Item.ProcessAnimation</c> would write from the
        /// Item's own fields (false: not due).</summary>
        internal delegate bool LegacyShadow(Item item, out int frame, out long due);

        private static readonly LegacyShadow LiveLegacy = LegacyAdvance;

        // Item.ProcessAnimation's computation from the Item's fields, writing nothing
        private static bool LegacyAdvance(Item item, out int frame, out long due)
        {
            frame = -1;
            due = 0;

            if (!(item.LastAnimationChangeTime < Time.Ticks))
            {
                return false;
            }

            byte dir = (byte)item.Layer;
            byte frameIndex = (byte)(item.AnimIndex + (item.ExecuteAnimation ? 1 : 0));
            ushort id = item.GetGraphicForAnimation();
            bool mirror = false;
            var animations = Client.Game.UO.Animations;
            animations.GetAnimDirection(ref dir, ref mirror);

            if (id < animations.MaxAnimationCount && dir < 5 &&
                Client.Game.UO.FileManager.Animations.TryCorpseFrame(id, item.UsedLayer, dir, frameIndex, out byte f))
            {
                frame = f;
            }

            due = Time.Ticks + Constants.CHARACTER_ANIMATION_DELAY;

            return true;
        }

        // ---- the checker -------------------------------------------------------------------------------------------------

        /// <summary>
        /// CUO_ANIM_ADVANCE_CHECK: the timer component and the ItemData must equal the Item's fields (a missed push otherwise);
        /// the legacy computation from the Item and the ECS rules from the component must give the same frame, due time and
        /// not-due answer, and neither may draw a random number (the state is compared around the ECS step).
        /// </summary>
        private static void Check<TSource>(Item item, in AnimationTimer component, in ItemData data, in TSource source, LegacyShadow legacy)
            where TSource : struct, ICorpseAnimationSource
        {
            AnimationTimer held = item.CorpseTimer;
            SyncChecks++;

            if (!Same(in component, in held) || data.Amount != item.Amount || data.Layer != (byte)item.Layer)
            {
                SyncDiffs++;
                Log1($"component of 0x{item.Serial:X8} differs from the Item: ecs {Describe(in component)} amount {data.Amount} layer {data.Layer} | item {Describe(in held)} amount {item.Amount} layer {(byte)item.Layer}");
            }

            RandomHelper.State start = RandomHelper.Save();
            bool legacyDue = legacy(item, out int legacyFrame, out long legacyTime);
            AnimationTimer ecs = component;
            bool ecsDue = CorpseAnimationRules.Advance(ref ecs, in data, in source, out int ecsFrame, out long ecsTime);
            RandomHelper.State after = RandomHelper.Save();
            Checks++;

            if (!after.Equals(start))
            {
                RandomDiffs++;
                Log1($"0x{item.Serial:X8}: a corpse advance drew a random number");
                RandomHelper.Restore(in start);
            }

            if (legacyDue != ecsDue || (ecsDue && (legacyFrame != ecsFrame || legacyTime != ecsTime)))
            {
                Diffs++;
                Log1($"advance of 0x{item.Serial:X8} (tick {Time.Ticks}): ecs due {ecsDue} frame {ecsFrame} next {ecsTime} | legacy due {legacyDue} frame {legacyFrame} next {legacyTime}");
            }
        }

        internal static bool Same(in AnimationTimer a, in AnimationTimer b) =>
            a.Frame == b.Frame && a.ExecuteAnimation == b.ExecuteAnimation && a.AnimDue == b.AnimDue && a.UsedLayer == b.UsedLayer;

        private static string Describe(in AnimationTimer t) =>
            $"frame {t.Frame} exec {t.ExecuteAnimation} due {t.AnimDue} usedLayer {t.UsedLayer}";

        private static void Log1(string message)
        {
            if (_logged++ < 25)
            {
                Log.Warn($"[anim-advance-check] items: {message}");
            }
        }

        internal static string Summary() =>
            $"[anim-advance-check] items: ecs advances {Ran}, not due {NotDue}, declined to Item.Update {Declined}; checked {Checks}, {Diffs} different, {RandomDiffs} random draws; component syncs checked {SyncChecks}, {SyncDiffs} different";
    }
}
