// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Game.Scenes
{
    /// <summary>Why the world view jumped: the next frames would show a half-built world.</summary>
    internal enum WorldDiscontinuity
    {
        None,
        /// <summary>The first world frame of this scene (login, or a scene reload): there is no previous frame.</summary>
        Entry,
        /// <summary>The camera moved further in one frame than walking can (a teleport, recall, gate, [go).</summary>
        Teleport,
        /// <summary>The map changed (the GPU world pass is recreated for the new facet).</summary>
        Facet,
        /// <summary>The GPU world pass was recreated on the same facet (its chunks and pages are gone).</summary>
        Recreated,
        /// <summary>The GPU world resumes after frames it didn't draw (the developer Legacy mode, or a skipped frame).</summary>
        Resume
    }

    /// <summary>
    /// Decides, per frame, whether the GPU world's frame is presented or the last presented world frame is held, so a world
    /// entry, teleport, facet change or world-pass recreation never shows unbuilt chunks (gaps) or missing atlas pages
    /// (black) on screen. Walking never holds: edge streaming is the prefetch's job. Holding applies only after a
    /// discontinuity, and lasts until the world pass reports its visible area ready or <see cref="TimeoutMs"/> passes.
    /// With no previous frame to hold (first entry, or the world target was recreated), the world area is black and fades
    /// in over <see cref="FadeMs"/> once ready. Pure logic: GameScene feeds it the camera and the world pass's readiness.
    /// </summary>
    internal sealed class WorldSettleGate
    {
        /// <summary>A camera move larger than this on either screen axis in one frame is a discontinuity (8 tiles).</summary>
        public const double DefaultJumpPixels = 8 * 44;
        public const double DefaultTimeoutMs = 1500;
        public const double DefaultFadeMs = 250;

        public double JumpPixels = DefaultJumpPixels;
        public double TimeoutMs = DefaultTimeoutMs;
        public double FadeMs = DefaultFadeMs;

        /// <summary>False: observe only (count unsettled frames, never hold), the A/B baseline (CUO_WORLD_HOLD=0).</summary>
        public bool HoldEnabled = true;

        private bool _started;
        private object _pass;
        private int _facet;
        private double _camX, _camY;
        private bool _resume;

        // the current episode: a discontinuity until the visible area is ready (or the timeout)
        private WorldDiscontinuity _episode;
        private double _episodeStart;
        private int _episodeHeld, _episodePresentedUnsettled;
        private bool _holding, _haveFrame, _readySeen, _timedOut;
        private double _fadeStart = -1;
        private float _fade = 1f;

        /// <summary>The world area's opacity over black this frame (1 = normal; 0 = black, nothing to hold yet).</summary>
        public float Fade => _fade;

        /// <summary>This frame's world is not presented: the caller streams only and leaves the world target untouched.</summary>
        public bool Holding => _holding;

        public WorldDiscontinuity Episode => _episode;

        public SettleStats Stats;

        public struct SettleStats
        {
            public long Presented, PresentedUnsettled, PresentedUnsettledSteady, PresentedUnsettledAfterTimeout;
            public long Held, HeldBlack, Holds, Timeouts, Episodes, LegacyFrames;
            public double HoldMsTotal, HoldMsMax, ReadyMsMax;

            public override string ToString() =>
                $"presented {Presented} (unsettled {PresentedUnsettled}: {PresentedUnsettledSteady} while walking, {PresentedUnsettledAfterTimeout} after a timeout, " +
                $"{PresentedUnsettled - PresentedUnsettledSteady - PresentedUnsettledAfterTimeout} in an episode without hold); " +
                $"episodes {Episodes}, holds {Holds}, held frames {Held} ({HeldBlack} black), hold ms total {HoldMsTotal:0} max {HoldMsMax:0}, " +
                $"ready ms max {ReadyMsMax:0}, timeouts {Timeouts}; legacy frames {LegacyFrames}";
        }

        /// <summary>Raised when an episode ends: kind, ms until ready (or the timeout), frames held, unsettled frames presented, timed out.</summary>
        public event Action<WorldDiscontinuity, double, int, int, bool> EpisodeEnded;

        /// <summary>
        /// Start of a GPU world frame. <paramref name="pass"/> identifies the world pass (a new instance = recreated),
        /// (<paramref name="cameraX"/>, <paramref name="cameraY"/>) is the camera centre in world pixels, and
        /// <paramref name="targetKept"/> says the world target still holds the last presented frame. Returns true when
        /// this frame must hold (stream only, don't draw into the world target).
        /// </summary>
        public bool Begin(double nowMs, object pass, int facet, double cameraX, double cameraY, bool targetKept)
        {
            var kind = WorldDiscontinuity.None;

            if (!_started)
            {
                kind = WorldDiscontinuity.Entry;
            }
            else if (facet != _facet)
            {
                kind = WorldDiscontinuity.Facet;
            }
            else if (!ReferenceEquals(pass, _pass))
            {
                kind = WorldDiscontinuity.Recreated;
            }
            else if (_resume)
            {
                kind = WorldDiscontinuity.Resume;
            }
            else if (Math.Abs(cameraX - _camX) > JumpPixels || Math.Abs(cameraY - _camY) > JumpPixels)
            {
                kind = WorldDiscontinuity.Teleport;
            }

            _started = true;
            _pass = pass;
            _facet = facet;
            _camX = cameraX;
            _camY = cameraY;
            _resume = false;

            if (!targetKept)
            {
                // the world target was recreated (resize, first frame): nothing left to hold
                _haveFrame = false;
            }

            if (kind != WorldDiscontinuity.None)
            {
                if (_episode == WorldDiscontinuity.None)
                {
                    _episode = kind;
                    _episodeStart = nowMs;
                    _episodeHeld = 0;
                    _episodePresentedUnsettled = 0;
                    _timedOut = false;
                    Stats.Episodes++;

                    if (HoldEnabled)
                    {
                        Stats.Holds++;
                    }
                }

                // a discontinuity during an episode (e.g. two teleports) keeps its start: the timeout bounds the whole hold
                _readySeen = false;

                if (kind == WorldDiscontinuity.Entry)
                {
                    _haveFrame = false;
                }
            }

            _holding = false;

            if (_episode != WorldDiscontinuity.None)
            {
                if (_readySeen)
                {
                    EndEpisode(nowMs, false);
                }
                else if (nowMs - _episodeStart >= TimeoutMs)
                {
                    EndEpisode(nowMs, true);
                }
                else
                {
                    _holding = HoldEnabled;
                }
            }

            if (_holding)
            {
                Stats.Held++;

                if (!_haveFrame)
                {
                    Stats.HeldBlack++;
                }

                _episodeHeld++;
                _fade = _haveFrame ? 1f : 0f;
            }
            else if (_fadeStart >= 0)
            {
                _fade = FadeMs <= 0 ? 1f : (float)Math.Clamp((nowMs - _fadeStart) / FadeMs, 0, 1);

                if (_fade >= 1f)
                {
                    _fadeStart = -1;
                }
            }
            else
            {
                _fade = 1f;
            }

            return _holding;
        }

        private void EndEpisode(double nowMs, bool timedOut)
        {
            double ms = nowMs - _episodeStart;

            if (_episodeHeld > 0)
            {
                Stats.HoldMsTotal += ms;
                Stats.HoldMsMax = Math.Max(Stats.HoldMsMax, ms);

                if (!_haveFrame)
                {
                    // released from black: fade the world in
                    _fadeStart = nowMs;
                }
            }

            if (timedOut)
            {
                Stats.Timeouts++;
            }
            else
            {
                Stats.ReadyMsMax = Math.Max(Stats.ReadyMsMax, ms);
            }

            var kind = _episode;
            _timedOut = timedOut;
            _episode = WorldDiscontinuity.None;
            EpisodeEnded?.Invoke(kind, ms, _episodeHeld, _episodePresentedUnsettled, timedOut);
        }

        /// <summary>End of the GPU world frame: whether everything the view needs was drawable (no chunk pending, no page
        /// missing).</summary>
        public void End(bool visibleReady)
        {
            if (_holding)
            {
                _readySeen |= visibleReady;

                return;
            }

            // presented: the world target now holds this frame
            _haveFrame = true;
            Stats.Presented++;

            if (_episode != WorldDiscontinuity.None)
            {
                // observe-only mode: the episode runs until the first ready frame
                if (visibleReady)
                {
                    _readySeen = true;
                }
                else
                {
                    _episodePresentedUnsettled++;
                    Stats.PresentedUnsettled++;
                }

                return;
            }

            if (!visibleReady)
            {
                Stats.PresentedUnsettled++;

                if (_timedOut)
                {
                    Stats.PresentedUnsettledAfterTimeout++;
                }
                else
                {
                    Stats.PresentedUnsettledSteady++;
                }
            }
            else
            {
                _timedOut = false;
            }
        }

        /// <summary>A world frame not drawn by the GPU world pass (developer Legacy mode): the next GPU frame resumes.</summary>
        public void OnOtherRenderer()
        {
            _resume = true;
            _holding = false;
            _haveFrame = true; // Legacy drew into the world target: that is the frame to hold
            _fade = 1f;
            Stats.LegacyFrames++;
        }
    }
}
