// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.IO;
using System.Runtime.InteropServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    // Driver `burst <name> <frames>` (needs CUO_BENCH_SHOTS): dumps the world render target and the composited back buffer of
    // every drawn frame, back to back, as raw RGBA (<name>_fNNN_<w>x<h>_world.rgba / _screen.rgba), and logs the player's
    // state per frame. Raw, not PNG, so the dump costs a readback and a write, not an encode: one-frame flashes stay
    // one frame apart. Used for the turn-flash report (a frame-to-frame diff outside the player sprite).
    internal static class FrameBurst
    {
        private static string _prefix;
        private static int _left, _index;
        private static Color[] _world, _screen;

        internal static bool Active => _left > 0;

        internal static void Request(string prefix, int frames)
        {
            _prefix = prefix;
            _left = frames;
            _index = 0;
            Log.Info($"[burst] start {Path.GetFileName(prefix)} frames {frames}");
        }

        // the centre Crop x Crop pixels (the camera keeps the player there): <stem>_<x0>_<y0>_<w>x<h>.rgba
        private const int Crop = 480;

        private static void WriteCrop(string stem, Color[] pixels, int width, int height)
        {
            int w = Math.Min(Crop, width), h = Math.Min(Crop, height);
            int x0 = (width - w) / 2, y0 = (height - h) / 2;
            byte[] bytes = new byte[w * h * 4];
            ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(pixels.AsSpan());

            for (int y = 0; y < h; y++)
            {
                src.Slice(((y0 + y) * width + x0) * 4, w * 4).CopyTo(bytes.AsSpan(y * w * 4));
            }

            File.WriteAllBytes($"{stem}_{x0}_{y0}_{w}x{h}.rgba", bytes);
        }

        internal static void AfterComposite(GraphicsDevice device, RenderTargets targets, int bw, int bh)
        {
            if (_left <= 0)
            {
                return;
            }

            string stem = $"{_prefix}_f{_index:000}";
            RenderTarget2D world = targets.WorldRenderTarget;

            if (world != null && !world.IsDisposed)
            {
                int n = world.Width * world.Height;

                if (_world == null || _world.Length != n)
                {
                    _world = new Color[n];
                }

                world.GetData(_world);
                WriteCrop($"{stem}_world", _world, world.Width, world.Height);
            }

            if (_screen == null || _screen.Length != bw * bh)
            {
                _screen = new Color[bw * bh];
            }

            device.GetBackBufferData(_screen);
            WriteCrop($"{stem}_screen", _screen, bw, bh);

            if (Client.Game.UO.World is { InGame: true, Player: { } p })
            {
                Log.Info(
                    $"[burst] {Path.GetFileName(_prefix)} f {_index} t {Time.Ticks} dir {(int)p.Direction} pos {p.X},{p.Y},{p.Z} " +
                    $"steps {p.Steps.Count} anim {p.AnimIndex} off {p.Offset.X:F1},{p.Offset.Y:F1},{p.Offset.Z:F1} " +
                    $"rp {p.RealScreenPosition.X},{p.RealScreenPosition.Y} group {Mobile.GetGroupForAnimation(p, p.GetGraphicForAnimation(), true)} " +
                    $"animDir {(int)p.GetDirectionForAnimation() & 7} walking {p.GetAnimGroupInput().Walking} animPending {Client.Game.UO.Animations?.PendingDirections ?? -1} " +
                    $"{Client.Game.GetScene<GameScene>()?.BurstState()}"
                );
            }

            _index++;
            _left--;

            if (_left == 0)
            {
                Log.Info($"[burst] done {Path.GetFileName(_prefix)} frames {_index}");
            }
        }
    }
}
#endif
