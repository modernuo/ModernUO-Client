// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// Dev-only light slot diagnosis (driver <c>lightdiag N</c>): for N drawn frames it records every light the frame
    /// offered (GameScene.AddLight) with its outcome, and at the light pass logs where the lights sit against the visible
    /// world area (centre in view, halo reaching in, or unable to reach it: only in the walk's square range), the source kinds
    /// and the graphics that took the most offers. After the last frame it logs the tiledata light-flagged statics with no
    /// light shape (light index 100 or more, or an empty light texture), and which of them the frames offered.
    /// </summary>
    internal static class LightSlotDiag
    {
        public const byte Added = 0, Refused = 2, Occluded = 3, BadId = 4, Culled = 5;

        private struct Offer
        {
            public int X, Y;
            public ushort Graphic;
            public byte Outcome, Kind, Id;
        }

        private static int _framesLeft, _frameNo;
        private static readonly Offer[] _offers = new Offer[16384];
        private static int _count, _lost;
        private static readonly int[] _byGraphic = new int[0x10000];
        private static readonly int[] _total = new int[0x10000];
        private static readonly byte[] _kindOf = new byte[0x10000];

        public static bool Armed => _framesLeft > 0;

        public static void Start(int frames)
        {
            _framesLeft = frames;
            _frameNo = 0;
            _count = _lost = 0;
            Array.Clear(_total);
            Log.Info($"[light-slots] start {frames} frames");
        }

        /// <summary>One AddLight call: <paramref name="outcome"/> as above, <paramref name="id"/> the light index stored (255
        /// when none was).</summary>
        public static void Record(GameObject obj, GameObject lightObject, int x, int y, byte outcome, byte id)
        {
            if (_count == _offers.Length)
            {
                _lost++;

                return;
            }

            ref Offer o = ref _offers[_count++];
            o.X = x;
            o.Y = y;
            o.Graphic = lightObject?.Graphic ?? 0;
            o.Outcome = outcome;
            o.Id = id;
            o.Kind = obj switch
            {
                Mobile => 3,
                Item => 2,
                Static or Multi => 1,
                _ => 0
            };
        }

        /// <summary>The light pass of an armed frame: (left, top, right, bottom) is the visible world area in the light
        /// list's coordinates.</summary>
        public static void EndFrame(int left, int top, int right, int bottom, int stored)
        {
            if (_framesLeft <= 0)
            {
                return;
            }

            LightsLoader lights = Client.Game.UO.FileManager.Lights;
            int maxHalf = (lights.MaxExtent(LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT) + 1) >> 1;
            int added = 0, refused = 0, occluded = 0, badId = 0, culled = 0, noShape = 0;
            int centerIn = 0, reach = 0, outside = 0, addedOutside = 0;
            Span<int> kinds = stackalloc int[4];
            Array.Clear(_byGraphic);

            for (int i = 0; i < _count; i++)
            {
                ref Offer o = ref _offers[i];
                int hw = maxHalf, hh = maxHalf;

                switch (o.Outcome)
                {
                    case Added:
                        added++;

                        if (!lights.HasShape(o.Id))
                        {
                            noShape++;
                        }

                        lights.Extent(o.Id, out int w, out int h);
                        hw = (w + 1) >> 1;
                        hh = (h + 1) >> 1;

                        break;
                    case Refused: refused++; break;
                    case Occluded: occluded++; break;
                    case BadId: badId++; break;
                    case Culled: culled++; break;
                }

                bool inView = o.X >= left && o.X <= right && o.Y >= top && o.Y <= bottom;
                bool reaches = o.X + hw >= left && o.X - hw <= right && o.Y + hh >= top && o.Y - hh <= bottom;

                if (inView)
                {
                    centerIn++;
                }
                else if (reaches)
                {
                    reach++;
                }
                else
                {
                    outside++;

                    if (o.Outcome == Added)
                    {
                        addedOutside++;
                    }
                }

                kinds[o.Kind]++;
                _byGraphic[o.Graphic]++;
                _total[o.Graphic]++;
                _kindOf[o.Graphic] = o.Kind;
            }

            Log.Info($"[light-slots] frame {_frameNo}: offered {_count} (added {added}, cap-refused {refused}, occluded {occluded}, id>=100 {badId}, culled {culled}), " +
                     $"added without a light shape {noShape}; lights in the list {stored}; centre in view {centerIn}, halo reaches in {reach}, cannot reach the view {outside} " +
                     $"(of them added {addedOutside}); sources: statics/multis {kinds[1]}, items {kinds[2]}, mobiles {kinds[3]}, other {kinds[0]}; " +
                     $"view {left},{top}..{right},{bottom}; top graphics {Top(_byGraphic, 10)}" + (_lost > 0 ? $"; {_lost} offers not recorded" : ""));

            _count = _lost = 0;
            _frameNo++;

            if (--_framesLeft == 0)
            {
                Log.Info($"[light-slots] all frames, top graphics {Top(_total, 20)}");
                LogTileData(lights);
            }
        }

        private static string Top(int[] counts, int n)
        {
            StringBuilder sb = new StringBuilder();
            StaticTiles[] data = Client.Game.UO.FileManager.TileData.StaticData;
            Span<int> best = stackalloc int[n];
            best.Fill(-1);

            for (int g = 0; g < counts.Length; g++)
            {
                if (counts[g] == 0)
                {
                    continue;
                }

                for (int k = 0; k < n; k++)
                {
                    if (best[k] < 0 || counts[g] > counts[best[k]])
                    {
                        for (int m = n - 1; m > k; m--)
                        {
                            best[m] = best[m - 1];
                        }

                        best[k] = g;

                        break;
                    }
                }
            }

            for (int k = 0; k < n && best[k] >= 0; k++)
            {
                int g = best[k];
                string name = g < data.Length ? data[g].Name : "";
                string kind = _kindOf[g] switch { 1 => "static", 2 => "item", 3 => "mobile", _ => "?" };
                byte layer = g < data.Length ? data[g].Layer : (byte)0;
                sb.Append($"0x{g:X4} x{counts[g]} ({kind} '{name}' light {layer}{(g < data.Length && data[g].IsLight ? "" : " notflag")}) ");
            }

            return sb.ToString();
        }

        private static void LogTileData(LightsLoader lights)
        {
            StaticTiles[] data = Client.Game.UO.FileManager.TileData.StaticData;
            int flagged = 0, noShape = 0, offeredNoShape = 0;
            StringBuilder sb = new StringBuilder();
            StringBuilder offered = new StringBuilder();

            for (int g = 0; g < data.Length; g++)
            {
                if (!data[g].IsLight)
                {
                    continue;
                }

                flagged++;

                if (lights.HasShape(data[g].Layer))
                {
                    continue;
                }

                noShape++;

                if (noShape <= 60)
                {
                    sb.Append($"0x{g:X4}:{data[g].Layer} ");
                }

                if (_total[g] > 0)
                {
                    offeredNoShape++;
                    offered.Append($"0x{g:X4} '{data[g].Name}' light {data[g].Layer} x{_total[g]}; ");
                }
            }

            Log.Info($"[light-slots] tiledata: {flagged} light-flagged static graphics, {noShape} without a light shape (graphic:light) {sb}");
            Log.Info($"[light-slots] offered in these frames without a shape: {offeredNoShape} graphics {offered}");
        }
    }
}
#endif
