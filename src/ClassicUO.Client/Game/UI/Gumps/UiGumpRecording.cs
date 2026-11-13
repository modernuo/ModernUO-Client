// SPDX-License-Identifier: BSD-2-Clause
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using System;
#if CUO_DEVTOOLS
using System.Diagnostics;
using ClassicUO.Utility.Logging;
#endif

namespace ClassicUO.Game.UI.Gumps
{
    internal sealed class UiGumpRecording
    {
        private readonly Gump _root;
        private readonly RenderLists _commands = new();
        private readonly Layer _sprites, _texts;
        private long _version = -1;
        private float _startDepth, _endDepth;
        private int _epoch = -1;
        private bool _supported, _debug;

        internal UiGumpRecording(Gump root)
        {
            _root = root;
            _sprites = new Layer(this, false);
            _texts = new Layer(this, true);
        }

        // All derived root types and every unaudited child type are always dirty.
        // In particular health bars/containers keep SelectedObject writes; journal, items, paperdoll,
        // minimap, animations, timers, scrolling and text entry continue their normal full walk.
        internal static bool IsTreeSupported(Control control)
        {
            Type type = control.GetType();
            bool supported = control is Gump
                ? type == typeof(Gump)
                : type == typeof(Label) || type == typeof(GumpPic) || type == typeof(Button) || type == typeof(ResizePic);
            if (!supported) return false;
            foreach (Control child in control.Children)
                if (child.Parent != control || !IsTreeSupported(child)) return false;
            return true;
        }

        internal static bool TryQueue(Gump root, RenderLists destination, ref float depth, bool enabled, bool recording)
        {
            if (!enabled || !recording || root.GetType() != typeof(Gump)) return false;
            (root.Recording ??= new UiGumpRecording(root)).Queue(destination, ref depth);
            return true;
        }

        internal void Queue(RenderLists destination, ref float depth)
        {
            bool changed = _version != _root.DrawVersion || _startDepth != depth || _epoch != TextureUploads.Epoch || _debug != CUOEnviroment.Debug;
            if (changed)
            {
                _debug = CUOEnviroment.Debug;
                _supported = IsTreeSupported(_root) && !CUOEnviroment.Debug;
                _version = _root.DrawVersion;
                _startDepth = depth;
                _epoch = TextureUploads.Epoch;
                _sprites.Fragment.Invalidate();
                _texts.Fragment.Invalidate();
            }
            if (!_supported)
            {
                FrameStats.Inc(FrameCounter.GumpsWalked);
                _root.AddToRenderLists(destination, _root.X, _root.Y, ref depth);
                return;
            }
            if (changed || !_sprites.Fragment.TexturesAlive() || !_texts.Fragment.TexturesAlive())
            {
                _commands.Clear();
                FrameStats.Inc(FrameCounter.GumpsWalked);
                _root.AddToRenderLists(_commands, _root.X, _root.Y, ref depth);
                _endDepth = depth;
                _sprites.Fragment.Invalidate();
                _texts.Fragment.Invalidate();
            }
            else
            {
                FrameStats.Inc(FrameCounter.GumpsReplayed);
                depth = _endDepth;
            }
            destination.AddGumpWithAtlas(GumpCommand.Deferred(_sprites, 0, 0, 0));
            destination.AddGumpNoAtlas(GumpCommand.Deferred(_texts, 0, 0, 0));
        }

        private sealed class Layer : IGumpDeferredDraw
        {
            private readonly UiGumpRecording _owner;
            private readonly bool _text;
            internal readonly SpriteFragment Fragment = new();
#if CUO_DEVTOOLS
            private readonly SpriteFragment _shadow = new();
#endif
            internal Layer(UiGumpRecording owner, bool text) { _owner = owner; _text = text; }
            public bool DrawDeferred(UltimaBatcher2D batcher, int x, int y, float depth, int arg)
            {
                if (!batcher.IsRecording)
                {
                    _owner._commands.DrawGumpLayer(batcher, _text);
                    return true;
                }
                if (!Fragment.Valid || !batcher.FragmentEnvironmentMatches(Fragment))
                {
                    var mark = batcher.BeginFragment();
                    _owner._commands.DrawGumpLayer(batcher, _text);
                    batcher.EndFragment(Fragment, mark);
                }
                else
                {
#if CUO_DEVTOOLS
                    if (Check)
                    {
                        // Re-walk the tree so the checker can catch missed invalidation.
                        ShadowCommands.Clear();
                        float freshDepth = _owner._startDepth;
                        _owner._root.AddToRenderLists(ShadowCommands, _owner._root.X, _owner._root.Y, ref freshDepth);
                        var mark = batcher.BeginFragment();
                        ShadowCommands.DrawGumpLayer(batcher, _text);
                        batcher.EndFragment(_shadow, mark);
                        Report(freshDepth == _owner._endDepth && Fragment.SameAs(_shadow), _owner._root, _text);
                        batcher.RewindFragment(mark);
                    }
#endif
                    batcher.AppendFragment(Fragment);
                }
                return true;
            }
        }
#if CUO_DEVTOOLS
        private static readonly bool Check = Environment.GetEnvironmentVariable("CUO_UI_DIRTY_CHECK") == "1";
        private static readonly RenderLists ShadowCommands = new();
        private static long _checks, _mismatches, _lastLog;
        private static void Report(bool same, Gump root, bool text)
        {
            _checks++;
            if (!same)
            {
                _mismatches++;
                Log.Warn($"[ui-dirty-check] mismatch {root.GetType().Name}, text={text}, version={root.DrawVersion}");
            }
            long now = Stopwatch.GetTimestamp();
            if (now - _lastLog > Stopwatch.Frequency * 5)
            {
                _lastLog = now;
                Log.Info($"[ui-dirty-check] on={Configuration.Settings.GlobalSettings.UiGumpDirty} checks={_checks} mismatches={_mismatches}");
            }
        }
#endif
    }
}
