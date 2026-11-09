// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Render;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// CLEANUP(release) T7b part 3 checker (<c>CUO_EFFECT_SPRITE_CHECK=N</c>: every Nth world frame, 1 = every frame), both
    /// renderers: for every effect the effect extract handles, the extract's output (<see cref="EffectSpriteSink"/>) is
    /// recorded first, then the view's (Legacy: the batcher calls through <see cref="UltimaBatcher2D.Capture"/>, with the
    /// rotation; Gpu: the instances GpuWorldRenderer's capture makes), and the two are compared in order; so are the drawn
    /// result, the light at the source, the blend mode (Legacy: the extract's against the object's, as the batcher capture
    /// does not see blend states; Gpu: in the instance) and the art entry's draw offset both store (item and static picking
    /// read it). With ecs_effect_sprites on the extract's output is drawn and the view's dropped; off, the reverse; the
    /// extract's lights stay (the same when they match). An effect the extract leaves to its view (no state, art the pack
    /// lacks) is drawn by the view and counted as "view path". A view sprite the Gpu capture could not map (FNA draws it)
    /// counts as a mismatch. Summary every 5 s: <c>[effect-sprite-check] … mismatched N</c>; the first
    /// <see cref="MAX_LOGGED"/> mismatches are logged. No allocation per effect.
    /// </summary>
    internal static class EffectSpriteCheck
    {
        private const int MAX_LOGGED = 20;

        public static readonly int Interval = LegacyEcsParity.ParseInterval(Environment.GetEnvironmentVariable("CUO_EFFECT_SPRITE_CHECK"));

        private static readonly Recorder _rec = new Recorder();
        private static int _frame, _logged;
        private static uint _nextSummary;
        private static long _frames, _effects, _legacy, _gpu, _drag, _rotated, _viewPath, _viewCalls, _ecsCalls, _lights;
        private static long _mismatched, _callMismatched, _drawnMismatched, _lightMismatched, _blendMismatched, _pickMismatched, _fna;

        /// <summary>Tests: wins over <see cref="Interval"/> (on every frame).</summary>
        internal static bool? Override;

        public static bool Active { get; private set; }

        /// <summary>Mismatch reports (tests).</summary>
        internal static Action<string> Observer;

        internal static long Mismatched => _mismatched;

        internal static long Effects => _effects;

        public static void BeginFrame()
        {
            Active = (Override ?? (Interval > 0 && ++_frame % Interval == 0)) && !GoldenCapture.Armed;

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

            Log.Info($"[effect-sprite-check] frames {_frames}, effects {_effects} (legacy {_legacy}, gpu {_gpu}; rotated {_rotated}, drag {_drag}, view path {_viewPath}), " +
                     $"compared view {_viewCalls} ecs {_ecsCalls}, lights {_lights}; ecs_effect_sprites {(EffectSpriteSink.On ? "on" : "off")}; " +
                     $"mismatched {_mismatched} (calls {_callMismatched}, drawn {_drawnMismatched}, lights {_lightMismatched}, blend {_blendMismatched}, " +
                     $"pick {_pickMismatched}, fna {_fna})");
        }

        /// <summary>Legacy: one effect of the effect list, extract and view recorded and compared; the switch's path is drawn.</summary>
        public static bool CompareLegacy(UltimaBatcher2D batcher, GameEffect e, float depth, bool sinkDraws)
        {
            GameScene scene = Client.Game.GetScene<GameScene>();
            int lightsBefore = scene?.LightCount ?? 0;
            Recorder r = _rec;
            r.Count = 0;
            r.Suppress = !sinkDraws;
            batcher.Capture = r;
            bool ecs, ecsDrawn;

            try
            {
                ecs = EffectSpriteSink.TryDrawLegacy(batcher, e, depth, true, out ecsDrawn);
            }
            finally
            {
                batcher.Capture = null;
            }

            _effects++;
            _legacy++;

            if (!ecs)
            {
                // left to the view: the extract drew nothing
                _viewPath++;

                return e.Draw(batcher, e.RealScreenPosition.X, e.RealScreenPosition.Y, depth);
            }

            EffectSpriteExtract x = EffectSpriteSink.Extract;
            int ecsEnd = r.Count;
            int lightsEcs = scene?.LightCount ?? 0;
            (ushort, int, int) pickEcs = PickEntry(x);

            r.Suppress = sinkDraws;
            batcher.Capture = r;
            bool viewDrawn;

            try
            {
                viewDrawn = e.Draw(batcher, e.RealScreenPosition.X, e.RealScreenPosition.Y, depth);
            }
            finally
            {
                batcher.Capture = null;
            }

            (ushort, int, int) pickView = PickEntry(x);
            int viewEnd = r.Count;
            Count(x);
            _ecsCalls += ecsEnd;
            _viewCalls += viewEnd - ecsEnd;
            string problem = null;

            if (viewEnd - ecsEnd != ecsEnd)
            {
                _callMismatched++;
                problem = $" calls view={viewEnd - ecsEnd} ecs={ecsEnd}";
            }
            else
            {
                for (int i = 0; i < ecsEnd; i++)
                {
                    if (!r.Buf[ecsEnd + i].Same(in r.Buf[i]))
                    {
                        _callMismatched++;
                        problem = $" call {i}: view {r.Buf[ecsEnd + i]} | ecs {r.Buf[i]}";

                        break;
                    }
                }
            }

            if (x.HasSprite && x.Sprite.Rotated && x.Sprite.Blend != e.Blend)
            {
                _blendMismatched++;
                problem += $" blend view={e.Blend} ecs={x.Sprite.Blend}";
            }

            problem = Finish(x, viewDrawn, ecsDrawn, pickView, pickEcs, scene, lightsBefore, lightsEcs, problem);
            Report(e, problem);

            return sinkDraws ? ecsDrawn : viewDrawn;
        }

        /// <summary>Gpu: the extract's instances of one effect against its view capture's (captured after them), and the rest
        /// as <see cref="CompareLegacy"/>. The caller keeps the switch's instances.</summary>
        public static void CompareGpu(GameEffect e, ReadOnlySpan<SpriteInstance> ecs, ReadOnlySpan<SpriteInstance> view, bool ecsDrawn, bool viewDrawn,
            bool viewFellBack, (ushort, int, int) pickEcs, (ushort, int, int) pickView, GameScene scene, int lightsBefore, int lightsEcs)
        {
            EffectSpriteExtract x = EffectSpriteSink.Extract;
            _effects++;
            _gpu++;
            Count(x);
            _viewCalls += view.Length;
            _ecsCalls += ecs.Length;
            string problem = null;

            if (viewFellBack)
            {
                _fna++;
                problem = " the view's sprite fell back to FNA";
            }

            if (view.Length != ecs.Length)
            {
                _callMismatched++;
                problem += $" instances view={view.Length} ecs={ecs.Length}";
            }
            else
            {
                for (int i = 0; i < view.Length; i++)
                {
                    if (!SameInstance(in view[i], in ecs[i]))
                    {
                        _callMismatched++;
                        problem += $" instance {i}: view {Describe(in view[i])} | ecs {Describe(in ecs[i])}";

                        break;
                    }
                }
            }

            problem = Finish(x, viewDrawn, ecsDrawn, pickView, pickEcs, scene, lightsBefore, lightsEcs, problem);
            Report(e, problem);
        }

        /// <summary>A Gpu effect the extract leaves to its view (no state, art the pack lacks): counted, nothing compared.</summary>
        public static void GpuViewPath()
        {
            _effects++;
            _gpu++;
            _viewPath++;
        }

        /// <summary>The art entry's draw offset of the extract's sprite (what the view stores too; picking reads it).</summary>
        public static (ushort, int, int) PickEntry(EffectSpriteExtract x)
        {
            if (!x.HasSprite)
            {
                return default;
            }

            ushort graphic = (ushort)x.Sprite.Sprite.Id;
            ref ArtEntry entry = ref Client.Game.UO.FileManager.Arts.GetEntry(graphic + 0x4000);

            return (graphic, entry.Width, entry.Height);
        }

        internal static bool SameInstance(in SpriteInstance a, in SpriteInstance b) =>
            a.X == b.X && a.Y == b.Y && a.Whf == b.Whf && a.Uv == b.Uv && a.PageHue == b.PageHue && a.Depth == b.Depth && a.Light == b.Light &&
            a.Corners == b.Corners;

        private static string Describe(in SpriteInstance i) =>
            $"at {i.X},{i.Y} whf {i.Whf:X8} uv {i.Uv:X8} pagehue {i.PageHue:X8} depth {i.Depth:X8} light {i.Light:X8} corners {i.Corners:X8}";

        private static void Count(EffectSpriteExtract x)
        {
            if (x.HasSprite)
            {
                if (x.Sprite.Rotated)
                {
                    _rotated++;
                }
                else
                {
                    _drag++;
                }
            }
        }

        // drawn, art entry and lights: the extract's lights [before, lightsEcs) against the view's [lightsEcs, end); the view's
        // are removed again
        private static string Finish(EffectSpriteExtract x, bool viewDrawn, bool ecsDrawn, (ushort, int, int) pickView, (ushort, int, int) pickEcs,
            GameScene scene, int lightsBefore, int lightsEcs, string problem)
        {
            if (viewDrawn != ecsDrawn)
            {
                _drawnMismatched++;
                problem += $" drawn view={viewDrawn} ecs={ecsDrawn}";
            }

            if (pickView != pickEcs)
            {
                _pickMismatched++;
                problem += $" art entry view={pickView} ecs={pickEcs}";
            }

            if (scene != null && EcsParity.LightsFit(lightsBefore, lightsEcs))
            {
                int end = scene.LightCount;
                int ecs = lightsEcs - lightsBefore, view = end - lightsEcs;
                _lights += view;
                bool same = view == ecs;

                for (int i = 0; same && i < ecs; i++)
                {
                    same = scene.LightAt(lightsBefore + i) == scene.LightAt(lightsEcs + i);
                }

                if (!same)
                {
                    _lightMismatched++;
                    problem += $" lights view={view} ecs={ecs}";
                }
            }

            scene?.TruncateLights(lightsEcs);

            return problem;
        }

        private static void Report(GameEffect e, string problem)
        {
            if (problem == null)
            {
                return;
            }

            _mismatched++;
            Observer?.Invoke(problem);

            if (_logged++ < MAX_LOGGED)
            {
                Log.Warn($"[effect-sprite-check] mismatch {e.GetType().Name} g=0x{e.Graphic:X4} anim 0x{e.AnimationGraphic:X4} blend {e.Blend} at {e.X},{e.Y},{e.Z}:{problem}");
            }
        }

        /// <summary>The Legacy batcher calls of both sides (texture, rect, position, hue, depth, rotation).</summary>
        internal sealed class Recorder : ISpriteCapture
        {
            public LegacyDraw[] Buf = new LegacyDraw[16];
            public int Count;
            public bool Suppress;

            private bool Record(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, float depth, SpriteCaptureMode mode, bool rotated,
                float rotation)
            {
                if (Count >= Buf.Length)
                {
                    Array.Resize(ref Buf, Buf.Length * 2);
                }

                Buf[Count++] = new LegacyDraw
                {
                    Texture = texture, Position = position, Source = source, Hue = hue, Depth = depth, Flip = flip, Mode = mode, Rotated = rotated,
                    Rotation = rotation
                };

                return Suppress;
            }

            bool ISpriteCapture.Draw(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, bool scaled, float depth, SpriteCaptureMode mode) =>
                Record(texture, position, source, hue, flip, depth, mode, false, 0f);

            bool ISpriteCapture.DrawRotated(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, float rotation, float depth) =>
                Record(texture, position, source, hue, false, depth, SpriteCaptureMode.Normal, true, rotation);

            bool ISpriteCapture.DrawSitted(Texture2D texture, Vector2 position, Rectangle source, Vector3 mod, Vector3 hue, bool flip, float depth) =>
                Record(texture, position, source, hue, flip, depth, SpriteCaptureMode.Sitted, false, 0f);
        }
    }
}
#endif
