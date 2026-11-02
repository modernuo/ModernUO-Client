// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// CLEANUP(release) T7b checker (<c>CUO_LEGACY_ECS_PARITY=N</c>: every Nth Legacy world frame): for each mobile and world
    /// item in the render lists, the view's batcher calls (texture, source rect, destination, hue vector, depth, flip,
    /// shadow/sitted with the sitting sections) are recorded through <see cref="UltimaBatcher2D.Capture"/>, then the
    /// <see cref="LegacySpriteSink"/>'s for the same object, and the two lists are compared in order; so are the view's side
    /// effects (drawn result, the mobile's FrameInfo and mirror bit, a corpse's mirror bit, the lights). With
    /// legacy_ecs_sprites on the sink's calls reach the batcher and the view's are dropped; off, the reverse. Zero-area calls
    /// (the views' empty last strips) draw nothing and are not recorded. A summary every 5 s: <c>[legacy-parity] … mismatched N</c>.
    /// No allocation per object; a mismatch's log line allocates (at most <see cref="MAX_LOGGED"/>).
    /// </summary>
    internal sealed class LegacyEcsParity : ISpriteCapture
    {
        private const int MAX_LOGGED = 40;

        public static readonly int Interval = ParseInterval(Environment.GetEnvironmentVariable("CUO_LEGACY_ECS_PARITY"));

        private static readonly LegacyEcsParity _rec = new LegacyEcsParity();
        private static int _frame, _logged;
        private static uint _nextSummary;
        private static long _frames, _objects, _mobiles, _items, _corpses, _viewPath, _viewDraws, _sinkDraws;
        private static long _mismatched, _drawMismatched, _drawnMismatched, _frameInfoMismatched, _flipMismatched, _lightMismatched, _lights;

        private LegacyDraw[] _buf = new LegacyDraw[256];
        private int _count;
        private bool _suppress;

        public static bool Active { get; private set; }

        internal static int ParseInterval(string v) => int.TryParse(v, out int n) && n > 0 ? n : 0;

        /// <summary>Client load, before any scene Update (see EcsParity.ConfigureLoaders and bench-000231): a direction first
        /// seen while a worker decodes it draws nothing in the view for a few frames, while the extract's FrameInfo already
        /// has its size. The checker compares paths, not load timing, so animations load synchronously while it is on.</summary>
        public static void ConfigureLoaders() => ConfigureLoaders(Environment.GetEnvironmentVariable("CUO_LEGACY_ECS_PARITY"));

        internal static void ConfigureLoaders(string parity)
        {
            if (ParseInterval(parity) > 0)
            {
                ClassicUO.Renderer.Animations.Animations.AsyncFirstSight = false;
            }
        }

        public static void BeginFrame()
        {
            Active = Interval > 0 && ++_frame % Interval == 0;

            if (Active)
            {
                _frames++;
            }

            if (Interval > 0 && Time.Ticks >= _nextSummary)
            {
                _nextSummary = Time.Ticks + 5000;

                if (_frames > 0)
                {
                    Summary();
                }
            }
        }

        public static void Summary()
        {
            if (Interval <= 0)
            {
                return;
            }

            Log.Info($"[legacy-parity] frames {_frames}, objects {_objects} (mobiles {_mobiles}, items {_items}, corpses {_corpses}, view path {_viewPath}), " +
                     $"calls view {_viewDraws} sink {_sinkDraws}, lights {_lights}; legacy_ecs_sprites {(LegacySpriteSink.On ? "on" : "off")}; " +
                     $"mismatched {_mismatched} (calls {_drawMismatched}, drawn {_drawnMismatched}, frameinfo {_frameInfoMismatched}, mirror {_flipMismatched}, lights {_lightMismatched})");
        }

        /// <summary>One object of a parity frame: the view and the sink, recorded and compared. <paramref name="sinkDraws"/>:
        /// the switch is on (the sink's calls are drawn, the view's dropped).</summary>
        public static bool Compare(UltimaBatcher2D batcher, GameObject obj, float depth, bool sinkDraws, GameScene scene)
        {
            LegacyEcsParity r = _rec;
            int lightsBefore = scene?.LightCount ?? 0;

            r._count = 0;
            r._suppress = sinkDraws;
            batcher.Capture = r;
            bool viewDrawn;
            var rng = new RngFork();
            rng.Begin();

            try
            {
                viewDrawn = obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
            }
            finally
            {
                batcher.Capture = null;
            }

            int viewEnd = r._count;
            Rectangle viewFi = obj.FrameInfo;
            bool viewFlip = obj.ViewFlipped;
            int lightsView = scene?.LightCount ?? 0;

            r._suppress = !sinkDraws;
            batcher.Capture = r;
            bool sinkDrawn, ecs;

            // the sink runs from the RNG state the view started from (animation group rolls), so both paths see the same
            // values; only the drawn path's resulting state is kept, so the checker leaves the RNG as without it
            rng.Second();

            try
            {
                sinkDrawn = LegacySpriteSink.DrawEcs(batcher, obj, depth, false, out ecs);
            }
            finally
            {
                batcher.Capture = null;
            }

            _objects++;

            if (!ecs)
            {
                // no mirror entity: the sink drew the view (a second time, for the record); nothing to compare. The second
                // draw's lights and RNG rolls go again.
                _viewPath++;
                scene?.TruncateLights(lightsView);
                rng.End(false);

                return viewDrawn;
            }

            rng.End(sinkDraws);

            int sinkEnd = r._count;
            _viewDraws += viewEnd;
            _sinkDraws += sinkEnd - viewEnd;
            string problem = null;

            if (sinkEnd - viewEnd != viewEnd)
            {
                _drawMismatched++;
                problem = $" calls view={viewEnd} sink={sinkEnd - viewEnd}";
            }
            else
            {
                for (int i = 0; i < viewEnd; i++)
                {
                    if (!r._buf[i].Same(in r._buf[viewEnd + i]))
                    {
                        _drawMismatched++;
                        problem = $" call {i}: view {r._buf[i]} | sink {r._buf[viewEnd + i]}";

                        break;
                    }
                }
            }

            if (viewDrawn != sinkDrawn)
            {
                _drawnMismatched++;
                problem += $" drawn view={viewDrawn} sink={sinkDrawn}";
            }

            if (obj is Mobile mobile)
            {
                _mobiles++;
                MobileSpriteExtract extract = mobile.World.Ecs.MobileExtract;

                if (mobile.World.Ecs.Mirror.TryGetEntity(mobile.Serial, out Friflo.Engine.ECS.Entity e) && e.HasComponent<AnimationState>())
                {
                    bool flip = (e.GetComponent<AnimationState>().Flags & AnimStateFlags.Mirror) != 0;

                    if (flip != viewFlip)
                    {
                        _flipMismatched++;
                        problem += $" mirror view={viewFlip} sink={flip}";
                    }
                }

                if (extract.FrameInfo != viewFi)
                {
                    _frameInfoMismatched++;
                    problem += $" frameinfo view={viewFi} sink={extract.FrameInfo}";
                }

                if (scene != null && EcsParity.LightsFit(lightsBefore, lightsView))
                {
                    LegacySpriteSink.AddMobileLights(mobile, extract);
                    problem = AddProblem(problem, CompareLights(scene, lightsBefore, lightsView));
                }
            }
            else if (obj is Item item)
            {
                ItemSpriteExtract items = LegacySpriteSink.ItemExtract;
                _items++;

                if (item.IsCorpse)
                {
                    _corpses++;
                }

                if (items.SetsFlipped && items.Flipped != viewFlip)
                {
                    _flipMismatched++;
                    problem += $" mirror view={viewFlip} sink={items.Flipped}";
                }

                if (scene != null && EcsParity.LightsFit(lightsBefore, lightsView))
                {
                    if (items.Light)
                    {
                        scene.AddLight(item, item, items.LightX, items.LightY);
                    }

                    problem = AddProblem(problem, CompareLights(scene, lightsBefore, lightsView));
                }
            }

            if (problem != null)
            {
                _mismatched++;

                if (_logged++ < MAX_LOGGED)
                {
                    Log.Warn($"[legacy-parity] mismatch {obj.GetType().Name} 0x{(obj as Entity)?.Serial ?? 0:X8} g=0x{obj.Graphic:X4} at {obj.X},{obj.Y},{obj.Z}:{problem}");
                }
            }

            return sinkDrawn;
        }

        /// <summary>A mismatch description plus one more part; null stays null when the part is null (string concatenation
        /// would turn null + null into "", a mismatch with no reason: bench-000244).</summary>
        internal static string AddProblem(string problem, string part) => part == null ? problem : problem + part;

        /// <summary>Runs two paths from one random state and keeps one path's resulting state.</summary>
        internal struct RngFork
        {
            private ClassicUO.Utility.RandomHelper.State _start, _first;

            public void Begin() => _start = ClassicUO.Utility.RandomHelper.Save();

            /// <summary>Between the paths: the second starts from the first's start state.</summary>
            public void Second()
            {
                _first = ClassicUO.Utility.RandomHelper.Save();
                ClassicUO.Utility.RandomHelper.Restore(in _start);
            }

            /// <summary>After both: <paramref name="keepSecond"/> = the second path's state stays, else the first's.</summary>
            public void End(bool keepSecond)
            {
                if (!keepSecond)
                {
                    ClassicUO.Utility.RandomHelper.Restore(in _first);
                }
            }
        }

        // the view's lights [before, viewEnd) against the sink's [viewEnd, LightCount), in order; the sink's are removed again
        private static string CompareLights(GameScene scene, int before, int viewEnd)
        {
            int end = scene.LightCount;
            int view = viewEnd - before, sink = end - viewEnd;
            _lights += view;
            bool same = view == sink;

            for (int i = 0; same && i < view; i++)
            {
                same = scene.LightAt(before + i) == scene.LightAt(viewEnd + i);
            }

            scene.TruncateLights(viewEnd);

            if (same)
            {
                return null;
            }

            _lightMismatched++;

            return $" lights view={view} sink={sink}";
        }

        private bool Record(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, float depth, SpriteCaptureMode mode, Vector3 mod)
        {
            if (source.Width <= 0 || source.Height <= 0)
            {
                return _suppress;
            }

            if (_count >= _buf.Length)
            {
                Array.Resize(ref _buf, _buf.Length * 2);
            }

            _buf[_count++] = new LegacyDraw
            {
                Texture = texture, Position = position, Source = source, Hue = hue, Depth = depth, Flip = flip, Mode = mode, SitMod = mod
            };

            return _suppress;
        }

        bool ISpriteCapture.Draw(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, bool scaled, float depth, SpriteCaptureMode mode) =>
            Record(texture, position, source, hue, flip, depth, mode, default);

        bool ISpriteCapture.DrawRotated(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, float rotation, float depth) =>
            Record(texture, position, source, hue, false, depth, SpriteCaptureMode.Normal, new Vector3(rotation, 0, 0));

        bool ISpriteCapture.DrawSitted(Texture2D texture, Vector2 position, Rectangle source, Vector3 mod, Vector3 hue, bool flip, float depth) =>
            Record(texture, position, source, hue, flip, depth, SpriteCaptureMode.Sitted, mod);

        /// <summary>Tests: compares two recorded call lists as <see cref="Compare"/> does.</summary>
        internal static int FirstDifference(ReadOnlySpan<LegacyDraw> view, ReadOnlySpan<LegacyDraw> sink)
        {
            int n = Math.Min(view.Length, sink.Length);

            for (int i = 0; i < n; i++)
            {
                if (!view[i].Same(in sink[i]))
                {
                    return i;
                }
            }

            return view.Length == sink.Length ? -1 : n;
        }

        /// <summary>Tests: the recorder alone (what one side's batcher calls become).</summary>
        internal static LegacyEcsParity NewRecorder(bool suppress) => new LegacyEcsParity { _suppress = suppress };

        internal ReadOnlySpan<LegacyDraw> Recorded => new ReadOnlySpan<LegacyDraw>(_buf, 0, _count);
    }
}
#endif
