// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer.Arts;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// perf-4 (<c>art_container_prefetch</c>, default off): first-sight static art for what windows draw from items that
    /// never reach a map tile, so <see cref="FirstSightArt"/> never requests it: a container's contents (ContainerGump's
    /// item controls) and the paperdoll's equipment slots. Their controls look the sprite up synchronously when they are
    /// built, so without this every new sprite decodes on the main thread, one after another, on the first open.
    /// <list type="bullet">
    /// <item><see cref="RequestContained"/>: an item went into an open container (0x24 marked it opened; 0x3C's items and
    /// 0x25 follow). The worker decode starts while the rest of the packets are read.</item>
    /// <item><see cref="PrefetchContents"/> / <see cref="Prefetch"/>: right before the window builds its controls, the
    /// sprites still missing are decoded on the workers in one bounded batch and awaited (<see cref="Art.Prefetch"/>,
    /// at most <see cref="TimeoutMs"/>), so the main thread waits for the slowest decode instead of doing each in turn.</item>
    /// </list>
    /// Hues don't matter to the decode; a stack's two or three copies are one sprite. Main thread only.
    /// </summary>
    internal static class ContentsArt
    {
        /// <summary>Most time one batch waits for its decodes (main thread).</summary>
        public const int TimeoutMs = 8;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ART_CONTAINER_PREFETCH=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_ART_CONTAINER_PREFETCH") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>Tests: the switch on this thread (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        /// <summary>Tests: sees every early request (the displayed graphic), also without a game to decode it.</summary>
        [ThreadStatic] internal static Action<ushort> RequestedForTests;

        internal delegate void BatchObserver(ReadOnlySpan<uint> graphics);

        /// <summary>Tests: sees every batch handed to <see cref="Art.Prefetch"/>, also without a game to decode it.</summary>
        [ThreadStatic] internal static BatchObserver PrefetchedForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.ArtContainerPrefetch;

        /// <summary>
        /// Whether ContainerGump draws <paramref name="item"/> (a positive amount; not a corpse's worn equipment outside the
        /// container layers; not a wearable face, beard or hair). Layer is the tile data's, as ContainerGump's.
        /// </summary>
        public static bool ShownInContainer(Item item, bool corpse)
        {
            if (item.Amount <= 0)
            {
                return false;
            }

            // no tile data (unit tests place items without a game): the amount alone decides
            if (Client.Game?.UO?.FileManager?.TileData == null)
            {
                return true;
            }

            var layer = (Layer)item.ItemData.Layer;

            if (corpse && item.Layer > 0 && !Constants.BAD_CONTAINER_LAYERS[(int)layer])
            {
                return false;
            }

            return !(item.ItemData.IsWearable && (layer == Layer.Face || layer == Layer.Beard || layer == Layer.Hair));
        }

        /// <summary>WorldWriter.PlaceInContainer: <paramref name="item"/> went into <paramref name="container"/>.</summary>
        public static void RequestContained(Item item, Entity container)
        {
            if (item == null || !(container is Item box) || !box.Opened || !Enabled || !ShownInContainer(item, box.IsCorpse))
            {
                return;
            }

            ushort graphic = item.DisplayedGraphic;
            RequestedForTests?.Invoke(graphic);

            if (FirstSightArt.On)
            {
                Client.Game.UO.Arts.RequestArt(graphic);
            }
        }

        /// <summary>ContainerGump.UpdateContents, before it builds the item controls of <paramref name="container"/>.</summary>
        public static void PrefetchContents(World world, Entity container)
        {
            if (container == null || !Enabled)
            {
                return;
            }

            bool corpse = container.Graphic == 0x2006;
            Span<uint> graphics = stackalloc uint[Art.PrefetchMaxBatch];
            int n = 0;

            foreach (Item item in world.State.Children(container))
            {
                if (n == graphics.Length)
                {
                    break;
                }

                if (ShownInContainer(item, corpse))
                {
                    n = AddDistinct(graphics, n, item.DisplayedGraphic);
                }
            }

            Flush(graphics.Slice(0, n));
        }

        /// <summary>
        /// Decodes the static art <paramref name="graphics"/> (repeats allowed) on the workers and waits for them (bounded).
        /// </summary>
        public static void Prefetch(ReadOnlySpan<uint> graphics)
        {
            if (graphics.Length < 2 || !Enabled)
            {
                return;
            }

            Span<uint> distinct = stackalloc uint[Art.PrefetchMaxBatch];
            int n = 0;

            for (int i = 0; i < graphics.Length && n < distinct.Length; i++)
            {
                n = AddDistinct(distinct, n, graphics[i]);
            }

            Flush(distinct.Slice(0, n));
        }

        private static int AddDistinct(Span<uint> set, int n, uint graphic)
        {
            if (set.Slice(0, n).Contains(graphic))
            {
                return n;
            }

            set[n] = graphic;

            return n + 1;
        }

        private static void Flush(ReadOnlySpan<uint> distinct)
        {
            // one sprite: nothing to overlap, the control's own lookup costs the same as a wait
            if (distinct.Length < 2)
            {
                return;
            }

            PrefetchedForTests?.Invoke(distinct);

            if (FirstSightArt.On)
            {
                Client.Game.UO.Arts.Prefetch(distinct, TimeoutMs);
            }
        }
    }
}
