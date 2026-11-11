// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Render;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// CLEANUP(release) T9 golden captures (<c>CUO_GOLDEN_DIR=dir</c>, driver <c>golden &lt;name&gt; [frames]</c>): before T9
    /// deletes the checkers that compare the ECS paths with the legacy views (CUO_ECS_PARITY's view capture,
    /// CUO_LEGACY_ECS_PARITY), this saves what they compared as text, so a later build can be checked against references
    /// instead of the deleted code (measure/t9-golden/goldencmp.py compares two captures).
    /// <list type="bullet">
    /// <item>Sprites, per armed frame: the world's dynamic objects in draw order, each with its draws. Gpu: the frame's
    /// dynamic <see cref="SpriteInstance"/>s as submitted, attributed to the object whose draw (or cached item run) appended
    /// them. Legacy: the batcher calls of the statics, animations, effects and transparent lists through a recording
    /// <see cref="ISpriteCapture"/> that lets every call through; the atlas texture is replaced by the pack sprite it holds
    /// (<see cref="PackSpriteMap"/>) and the source's offset in it.</item>
    /// <item>State, at the command: every <c>FindItemByLayer</c> read of every mobile (what CUO_EQUIP_READ_CHECK compared) and
    /// every parent's <c>World.State.Children</c> (CUO_CONTENTS_CHECK).</item>
    /// </list>
    /// On an armed frame the Legacy parity and effect-sprite checkers stand down (they set the batcher's capture themselves).
    /// Off (no armed frame) the hooks cost a static bool read per drawn object.
    /// </summary>
    internal sealed class GoldenCapture : ISpriteCapture
    {
        public const int FORMAT = 1;

        public static readonly string Dir = Environment.GetEnvironmentVariable("CUO_GOLDEN_DIR");

        private static readonly GoldenCapture _rec = new GoldenCapture();
        private static string _name;
        private static World _world;
        private static int _framesLeft, _frameIndex;
        private static bool _legacy;

        private static GameObject[] _markObj;
        private static int[] _markAt;
        private static int _marks;

        private LegacyCall[] _calls;
        private int _count;

        /// <summary>A capture is due on the next world frame(s).</summary>
        public static bool Armed => _framesLeft > 0;

        /// <summary>Inside an armed frame's world draw: the hooks mark objects.</summary>
        public static bool Recording { get; private set; }

        /// <summary>
        /// Driver <c>goldenmobiles off</c>: mobiles stay out of the render queue (GameScene.PushToRenderQueue) on both
        /// renderers. The large scene's NPCs turn on their own (ModernUO's AI sets Direction on frozen NPCs too) and cover its
        /// whole viewport, so its world shots are taken without them; their sprites are compared through the text captures.
        /// </summary>
        public static bool HideMobiles;

        private struct LegacyCall
        {
            public uint Ref;
            public bool Mapped;
            public Point SrcOffset;
            public Rectangle Source;
            public Vector2 Position;
            public Vector3 HueVec, Mod;
            public bool Flip, Rotated;
            public SpriteCaptureMode Mode;
            public float Depth, Rotation;
        }

        /// <summary>Driver: dumps the state now and arms <paramref name="frames"/> sprite captures. False without CUO_GOLDEN_DIR.</summary>
        public static bool Request(World world, string name, int frames)
        {
            if (string.IsNullOrEmpty(Dir))
            {
                return false;
            }

            Directory.CreateDirectory(Dir);
            _name = name;
            _world = world;
            _frameIndex = 0;
            _framesLeft = Math.Max(1, frames);
            // the buffers exist only once a capture is asked for: the hooks read Recording every frame in every dev build
            _markObj ??= new GameObject[1024];
            _markAt ??= new int[1024];
            _rec._calls ??= new LegacyCall[4096];
            WriteState(world, Path.Combine(Dir, $"{name}.state.txt"), name);

            return true;
        }

        public static void Mark(GameObject obj, int at)
        {
            // a draw that falls back to another marked draw (an effect the Gpu capture takes from its view)
            if (_marks > 0 && ReferenceEquals(_markObj[_marks - 1], obj) && _markAt[_marks - 1] == at)
            {
                return;
            }

            if (_marks == _markObj.Length)
            {
                Array.Resize(ref _markObj, _marks * 2);
                Array.Resize(ref _markAt, _marks * 2);
            }

            _markObj[_marks] = obj;
            _markAt[_marks++] = at;
        }

        // ---- Gpu: GpuWorldRenderer.BeginFrame .. Render

        public static void BeginGpu()
        {
            if (Armed && !Recording)
            {
                Recording = true;
                _legacy = false;
                _marks = 0;
            }
        }

        public static void EndGpu(ReadOnlySpan<SpriteInstance> dynamic)
        {
            if (!Recording || _legacy)
            {
                return;
            }

            Recording = false;
            string path = FramePath("gpu");

            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                Header(w, "gpu");
                int mark = 0;

                for (int i = 0; i < dynamic.Length; i++)
                {
                    mark = Objects(w, mark, i, i == 0);
                    ref readonly SpriteInstance s = ref dynamic[i];
                    w.Write("G ");
                    w.Write(s.X.ToString(CultureInfo.InvariantCulture));
                    w.Write(' ');
                    w.Write(s.Y.ToString(CultureInfo.InvariantCulture));
                    w.Write(FormattableString.Invariant($" {s.Whf:X8} {s.Uv:X8} {s.PageHue:X8} {s.Depth:X8} {s.Light:X8} {s.Corners:X8}"));
                    w.Write('\n');
                }

                Objects(w, mark, int.MaxValue, dynamic.Length == 0);
                w.Write($"end {dynamic.Length} sprites\n");
            }

            Done(path, dynamic.Length);
        }

        // ---- Legacy: RenderLists' world-object draw

        public static void BeginLegacy(UltimaBatcher2D batcher)
        {
            if (Armed && !Recording && batcher.Capture == null)
            {
                Recording = true;
                _legacy = true;
                _marks = 0;
                _rec._count = 0;
                batcher.Capture = _rec;
            }
        }

        public static void MarkLegacy(GameObject obj) => Mark(obj, _rec._count);

        public static void EndLegacy(UltimaBatcher2D batcher)
        {
            if (!Recording || !_legacy)
            {
                return;
            }

            Recording = false;

            if (ReferenceEquals(batcher.Capture, _rec))
            {
                batcher.Capture = null;
            }

            string path = FramePath("legacy");
            int n = _rec._count;

            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                Header(w, "legacy");
                int mark = 0;

                for (int i = 0; i < n; i++)
                {
                    mark = Objects(w, mark, i, i == 0);
                    ref LegacyCall c = ref _rec._calls[i];
                    w.Write(string.Create(CultureInfo.InvariantCulture,
                        $"L {(c.Mapped ? c.Ref.ToString("X8") : "-")} {c.SrcOffset.X} {c.SrcOffset.Y} {c.Source.Width} {c.Source.Height} {F(c.Position.X)} {F(c.Position.Y)} {F(c.HueVec.X)} {F(c.HueVec.Y)} {F(c.HueVec.Z)} {(c.Flip ? 1 : 0)} {(int)c.Mode} {F(c.Depth)} {(c.Rotated ? F(c.Rotation) : "-")} {F(c.Mod.X)} {F(c.Mod.Y)} {F(c.Mod.Z)}\n"));
                }

                Objects(w, mark, int.MaxValue, n == 0);
                w.Write($"end {n} sprites\n");
            }

            Done(path, n);
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string FramePath(string renderer) => Path.Combine(Dir, $"{_name}.{renderer}.f{_frameIndex}.txt");

        private static void Done(string path, int sprites)
        {
            Log.Info($"[golden] {Path.GetFileName(path)}: {_marks} objects, {sprites} sprites");
            _frameIndex++;
            _framesLeft--;

            for (int i = 0; i < _marks; i++)
            {
                _markObj[i] = null;
            }

            _marks = 0;
        }

        private static void Header(StreamWriter w, string renderer)
        {
            var s = Configuration.Settings.GlobalSettings;
            w.Write($"golden {FORMAT}\n");
            w.Write($"name {_name} frame {_frameIndex} renderer {renderer}\n");
            w.Write($"paths gpu_ecs_sprites={(s.GpuEcsSprites ? 1 : 0)} legacy_ecs_sprites={(LegacySpriteSink.On ? 1 : 0)} " +
                    $"ecs_effect_sprites=1 gpu_prepare_item_cache={(s.GpuPrepareItemCache ? 1 : 0)} " +
                    $"hide_mobiles={(HideMobiles ? 1 : 0)}\n");

            if (_world?.Player is { } p)
            {
                w.Write(FormattableString.Invariant($"player 0x{p.Serial:X8} g=0x{p.Graphic:X4} {p.X} {p.Y} {p.Z} d={(int)p.Direction} map {_world.MapIndex}\n"));
            }
        }

        // the O lines of the marks that start at or before draw index <at>; a draw before any mark goes to "O - ?"
        private static int Objects(StreamWriter w, int mark, int at, bool first)
        {
            if (first && (_marks == 0 || _markAt[0] > 0) && at != int.MaxValue)
            {
                w.Write("O - ?\n");
            }

            while (mark < _marks && _markAt[mark] <= at)
            {
                // several marks at one index: the earlier objects drew nothing
                WriteObject(w, _markObj[mark]);
                mark++;
            }

            return mark;
        }

        private static void WriteObject(StreamWriter w, GameObject obj)
        {
            if (obj == null)
            {
                // Legacy: the gump sprites and texts drawn after the transparent list
                w.Write("O - gumps\n");

                return;
            }

            char kind = obj switch
            {
                Mobile => 'M',
                Item => 'I',
                GameEffect => 'E',
                Static => 'S',
                Multi => 'X',
                _ => 'U'
            };

            uint serial = (obj as Entity)?.Serial ?? 0;
            int dir = obj is Mobile m ? (int)m.Direction : -1;
            w.Write(FormattableString.Invariant($"O {kind} 0x{serial:X8} g=0x{obj.Graphic:X4} h=0x{obj.Hue:X4} {obj.X} {obj.Y} {obj.Z} d={dir} a={(Animated(obj) ? 1 : 0)}\n"));
        }

        /// <summary>Whether the object's sprites change with time (the compare's phase rule): mobiles, effects, corpses and
        /// tiledata-animated art.</summary>
        private static bool Animated(GameObject obj)
        {
            if (obj is Mobile || obj is GameEffect)
            {
                return true;
            }

            if (obj is Item item && item.IsCorpse)
            {
                return true;
            }

            var tiles = Client.Game.UO.FileManager.TileData.StaticData;

            return obj.Graphic < tiles.Length && tiles[obj.Graphic].IsAnimated;
        }

        // ---- state: equipment reads and contents

        private static void WriteState(World world, string path, string name)
        {
            var owners = new List<Entity>();

            foreach (Mobile m in world.Mobiles.Values)
            {
                if (!m.IsDestroyed)
                {
                    owners.Add(m);
                }
            }

            foreach (Item i in world.Items.Values)
            {
                if (!i.IsDestroyed && world.State.HasChildren(i))
                {
                    owners.Add(i);
                }
            }

            owners.Sort(static (a, b) => a.Serial.CompareTo(b.Serial));
            int equipped = 0, children = 0;

            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.Write($"golden-state {FORMAT}\n");
                w.Write($"name {name}\n");

                if (world.Player is { } p)
                {
                    w.Write(FormattableString.Invariant($"player 0x{p.Serial:X8} g=0x{p.Graphic:X4} {p.X} {p.Y} {p.Z} d={(int)p.Direction} map {world.MapIndex}\n"));
                }

                var sb = new StringBuilder();

                foreach (Entity e in owners)
                {
                    if (e is Mobile mob)
                    {
                        sb.Clear();
                        int n = 0;

                        for (int l = (int)Layer.OneHanded; l <= (int)Layer.Bank; l++)
                        {
                            if (mob.FindItemByLayer((Layer)l) is { } worn)
                            {
                                sb.Append(FormattableString.Invariant($" {l:X2}=0x{worn.Serial:X8}/0x{worn.Graphic:X4}/0x{worn.Hue:X4}"));
                                n++;
                            }
                        }

                        equipped += n;
                        w.Write(FormattableString.Invariant($"Q 0x{mob.Serial:X8} g=0x{mob.Graphic:X4} h=0x{mob.Hue:X4} {mob.X} {mob.Y} {mob.Z} n={n} r={(mob == world.Player ? 1 : 0)}:{sb}\n"));
                    }

                    if (!world.State.HasChildren(e))
                    {
                        continue;
                    }

                    sb.Clear();
                    int cn = 0;

                    foreach (Item c in world.State.Children(e))
                    {
                        sb.Append(FormattableString.Invariant($" 0x{c.Serial:X8}/0x{c.Graphic:X4}/0x{c.Hue:X4}/x{c.Amount}/l{(int)c.Layer:X2}@{c.X},{c.Y}"));
                        cn++;
                    }

                    children += cn;
                    char kind = e is Mobile ? 'M' : 'I';
                    // r=1: the player or held by the player (the test account's own items drift between sessions: goldencmp's state_ignore_player)
                    bool mine = e == world.Player || e is Item ei && world.Player != null && ei.RootContainer == world.Player.Serial;
                    w.Write(FormattableString.Invariant($"C {kind} 0x{e.Serial:X8} g=0x{e.Graphic:X4} h=0x{e.Hue:X4} {e.X} {e.Y} {e.Z} in=0x{(e as Item)?.Container ?? 0:X8} n={cn} r={(mine ? 1 : 0)}:{sb}\n"));
                }

                w.Write($"end {owners.Count} owners, {equipped} worn reads, {children} children\n");
            }

            Log.Info($"[golden] {Path.GetFileName(path)}: {owners.Count} owners, {equipped} worn reads, {children} children");
        }

        // ---- the Legacy recorder: every call goes on to the batcher

        private bool Record(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, float depth, SpriteCaptureMode mode, Vector3 mod,
            bool rotated = false, float rotation = 0f)
        {
            if (source.Width <= 0 || source.Height <= 0)
            {
                return false;
            }

            if (_count == _calls.Length)
            {
                Array.Resize(ref _calls, _count * 2);
            }

            ref LegacyCall c = ref _calls[_count++];
            c.Mapped = PackSpriteMap.TryFind(texture, source, out c.Ref, out Point origin);
            c.SrcOffset = c.Mapped ? new Point(source.X - origin.X, source.Y - origin.Y) : Point.Zero;
            c.Source = source;
            c.Position = position;
            c.HueVec = hue;
            c.Mod = mod;
            c.Flip = flip;
            c.Mode = mode;
            c.Depth = depth;
            c.Rotated = rotated;
            c.Rotation = rotation;

            return false;
        }

        bool ISpriteCapture.Draw(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, bool scaled, float depth, SpriteCaptureMode mode) =>
            Record(texture, position, source, hue, flip, depth, mode, default);

        bool ISpriteCapture.DrawRotated(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, float rotation, float depth) =>
            Record(texture, position, source, hue, false, depth, SpriteCaptureMode.Normal, default, true, rotation);

        bool ISpriteCapture.DrawSitted(Texture2D texture, Vector2 position, Rectangle source, Vector3 mod, Vector3 hue, bool flip, float depth) =>
            Record(texture, position, source, hue, flip, depth, SpriteCaptureMode.Sitted, mod);
    }
}
#endif
