// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// First-sight static art requests for what the chunk loader's block scan can't see: ground items and multi components
    /// (houses, boats), which arrive from the server after their chunk was built, and the frames of animated tiles (the
    /// view draws graphic + the frame offset AnimatedStaticsManager advances, never the graphic the block names).
    /// <list type="bullet">
    /// <item>The decode runs on a worker (Art.RequestArt, FirstSightDecoder); Map.UpdateLoading uploads it inside the chunk
    /// budget, as for chunk art.</item>
    /// <item>Requests are speculative and cost O(1) once the sprite is in the atlas. Nothing waits for them: the Gpu world
    /// draws items and multis from the pack, and a lookup that needs a sprite before its upload (a Legacy draw, the pick
    /// test of an item under the mouse) still decodes it on the spot, as before.</item>
    /// <item>Off when Map.ArtWait is off (CUO_ART_SYNC=1 or CUO_CHUNK_SYNC=1): nothing would drain the decodes.</item>
    /// </list>
    /// Main thread only.
    /// </summary>
    internal static class FirstSightArt
    {
        /// <summary>
        /// Requests static art <paramref name="graphic"/> and, when <paramref name="frames"/> is set and the tile is
        /// animated, every frame of its animation. True when <paramref name="graphic"/> itself needs nothing (in the atlas,
        /// absent, failed, or requests off).
        /// </summary>
        public static bool RequestStatic(ushort graphic, bool frames)
        {
            if (!Map.ArtWait)
            {
                return true;
            }

            bool ready = Client.Game.UO.Arts.RequestArt(graphic);

            if (frames)
            {
                RequestFrames(graphic);
            }

            return ready;
        }

        /// <summary>
        /// As <see cref="RequestStatic"/> for the frame an animated tile shows now (graphic + its AnimOffset), which is what
        /// the view draws; the same as <paramref name="graphic"/> for a tile that isn't animated.
        /// </summary>
        public static bool RequestCurrentFrame(ushort graphic)
        {
            if (!Map.ArtWait)
            {
                return true;
            }

            var fm = Client.Game.UO.FileManager;

            if (graphic >= fm.TileData.StaticData.Length || !fm.TileData.StaticData[graphic].IsAnimated)
            {
                return true;
            }

            int offset = fm.Arts.GetEntry(graphic + 0x4000).AnimOffset;

            return offset == 0 || Client.Game.UO.Arts.RequestArt((ushort)(graphic + offset));
        }

        /// <summary>Requests every frame of animated tile <paramref name="graphic"/> (its AnimData offsets); nothing for other tiles.</summary>
        public static unsafe void RequestFrames(ushort graphic)
        {
            if (!Map.ArtWait)
            {
                return;
            }

            var fm = Client.Game.UO.FileManager;

            if (graphic >= fm.TileData.StaticData.Length || !fm.TileData.StaticData[graphic].IsAnimated
                || !fm.AnimData.TryGetAnimData(graphic, out var info))
            {
                return;
            }

            var arts = Client.Game.UO.Arts;
            int count = info.FrameCount < 64 ? info.FrameCount : 64;

            for (int i = 0; i < count; i++)
            {
                arts.RequestArt((ushort)(graphic + info.FrameData[i]));
            }
        }

        /// <summary>
        /// Ground items and multi components placed on the map whose art was not in the atlas yet (a worker decode queued
        /// or in flight); FrameStats (art_obj) resets it.
        /// </summary>
        public static int FrameObjectRequests;

        /// <summary>A ground item or multi component was placed on the map (GameObject.AddToTile): request what its view draws.</summary>
        public static void RequestPlaced(ushort graphic)
        {
            if (Map.ArtWait && !RequestStatic(graphic, frames: true))
            {
                FrameObjectRequests++;
            }
        }
    }
}
