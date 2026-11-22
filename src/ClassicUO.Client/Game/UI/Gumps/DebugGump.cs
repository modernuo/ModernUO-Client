// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using System;
using System.Xml;
using Uo.Buffers;

namespace ClassicUO.Game.UI.Gumps
{
    internal class DebugGump : Gump, IGumpDeferredDraw
    {
        private static Point _last_position = new Point(-1, -1);

        private uint _timeToUpdate;
        private readonly AlphaBlendControl _alphaBlendControl;
        // G2: the shown text lives in this buffer and is drawn from it; a refresh that formats the same text does nothing
        private char[] _text = new char[256];
        private int _textLength;

        public DebugGump(World world, int x, int y) : base(world, 0, 0)
        {
            CanMove = true;
            CanCloseWithEsc = false;
            CanCloseWithRightClick = true;
            AcceptMouseInput = true;
            AcceptKeyboardInput = false;

            Width = 100;
            Height = 50;
            X = _last_position.X <= 0 ? x : _last_position.X;
            Y = _last_position.Y <= 0 ? y : _last_position.Y;

            Add
            (
                _alphaBlendControl = new AlphaBlendControl(.7f)
                {
                    Width = Width, Height = Height
                }
            );

            LayerOrder = UILayer.Over;

            WantUpdateSize = true;
        }

        public bool IsMinimized { get; set; }

        public override GumpType GumpType => GumpType.Debug;

        protected override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left)
            {
                IsMinimized = !IsMinimized;

                return true;
            }

            return false;
        }

        public override void Update()
        {
            base.Update();

            if (IsDisposed)
            {
                return;
            }

            if (Time.Ticks > _timeToUpdate)
            {
                _timeToUpdate = Time.Ticks + 100;

                GameScene scene = Client.Game.GetScene<GameScene>();
                Span<char> span = stackalloc char[256];
                ValueStringBuilder sb = new ValueStringBuilder(span);
                sb.Append($"{CUOEnviroment.CurrentLoopRate} loops/s | ");

                if (IsMinimized && scene != null)
                {
                    // the player-facing rate, then the measurement: frames drawn per second, and the rate the last frame's build time allows
                    sb.Append($"- FPS: {CUOEnviroment.ShownRefreshRate} (drawn {CUOEnviroment.CurrentRefreshRate}/s, possible {ClassicUO.Loop.FpsDisplay.Possible(CUOEnviroment.LastFrameBuildMs)}/s, frame build {CUOEnviroment.LastFrameBuildMs:0.00} ms), Zoom: {(!World.InGame ? 1f : scene.Camera.Zoom):0.00}, Total Objs: {scene.RenderedObjectsCount}\n");
                    sb.Append($"- CUO version: {CUOEnviroment.Version}, Client version: {Client.Game.UO.VersionText}\n");

                    sb.Append("- CharPos: ");

                    if (World.InGame)
                    {
                        sb.Append($"{World.Player.X}, {World.Player.Y}, {World.Player.Z}");
                    }
                    else
                    {
                        sb.Append("0xFFFF, 0xFFFF, 0");
                    }

                    // Point.ToString's text
                    Point mouse = Mouse.Position;
                    sb.Append($"\n- Mouse: {{X:{mouse.X} Y:{mouse.Y}}}\n- InGamePos: ");

                    if (SelectedObject.Object is GameObject gobj)
                    {
                        sb.Append($"{gobj.X}, {gobj.Y}, {gobj.Z}");
                    }
                    else
                    {
                        sb.Append("0xFFFF, 0xFFFF, 0");
                    }

                    sb.Append("\n- Selected: ");
                    Span<char> selected = stackalloc char[256];
                    sb.Append(selected.Slice(0, FormatObject(selected, SelectedObject.Object)));
                    sb.Append("\n");

                    if (Profiler.Enabled)
                    {
                        double timeDraw = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME).TimeInContext;
                        double timeDrawWorld = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD).TimeInContext;
                        double timeDrawWorldPrepare = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE).TimeInContext;
                        double timeDrawUi = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME_UI).TimeInContext;

                        double timeUpdate = Profiler.GetContext(Profiler.ProfilerContext.UPDATE_WORLD).TimeInContext;

                        double timeTotal = Profiler.TrackedTime;

                        double avgDrawMs = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME).AverageTime;

                        sb.Append("- Profiling\n");
                        sb.Append($"    - Draw:{100d * (timeDraw / timeTotal):0.0}% (Prepare:{100d * (timeDrawWorldPrepare / timeTotal):0.0}% World:{100d * (timeDrawWorld / timeTotal):0.0}%, UI:{100d * (timeDrawUi / timeTotal):0.0}%)\n");
                        sb.Append($"    - Update:{100d * (timeUpdate / timeTotal):0.0}%\n");
                        sb.Append($"    - AvgDraw:{avgDrawMs:0.0}ms {CUOEnviroment.CurrentRefreshRate} presented FPS\n");
                    }
                }
                else
                {
                    var cameraZoomCount = (int)((scene.Camera.ZoomMax - scene.Camera.ZoomMin) / scene.Camera.ZoomStep);
                    var cameraZoomIndex = cameraZoomCount - (int)((scene.Camera.ZoomMax - scene.Camera.Zoom) / scene.Camera.ZoomStep);

                    Span<char> small = stackalloc char[64];
                    sb.Append(small.Slice(0, FormatSmall(small, CUOEnviroment.ShownRefreshRate, scene != null && cameraZoomIndex != 5, !World.InGame ? 1f : scene.Camera.Zoom)));
                }

                if (!sb.AsSpan().SequenceEqual(_text.AsSpan(0, _textLength)))
                {
                    if (sb.Length > _text.Length)
                    {
                        _text = new char[Math.Max(sb.Length, _text.Length * 2)];
                    }

                    sb.AsSpan().CopyTo(_text);
                    _textLength = sb.Length;

                    Vector2 size = Fonts.Bold.MeasureString(_text.AsSpan(0, _textLength));

                    _alphaBlendControl.Width = Width = (int) (size.X + 20);
                    _alphaBlendControl.Height = Height = (int) (size.Y + 20);

                    WantUpdateSize = true;
                }

                sb.Dispose();
            }

            // visual-due tracking: the next timed change
            ClassicUO.Loop.VisualDue.At(_timeToUpdate, ClassicUO.Loop.VisualSource.Gump);
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (!base.AddToRenderLists(renderLists, x, y, ref layerDepthRef))
            {
                return false;
            }
            float layerDepth = layerDepthRef;

            renderLists.AddGumpNoAtlas(GumpCommand.Deferred(this, x, y, layerDepth));
            return true;
        }

        bool IGumpDeferredDraw.DrawDeferred(UltimaBatcher2D batcher, int x, int y, float layerDepth, int arg)
        {
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            batcher.DrawString
            (
                Fonts.Bold,
                _text.AsSpan(0, _textLength),
                x + 10,
                y + 10,
                hueVector,
                layerDepth
            );
            return true;
        }

        // the collapsed text: "FPS: n", the player-facing counter (the display rate while present on change runs,
        // FpsDisplay.Shown; never the idle draw rate), and the zoom unless it is at the default step
        internal static int FormatSmall(Span<char> destination, uint shownRate, bool showZoom, float zoom)
        {
            int n = 0;
            UiFormat.Put(destination, ref n, "FPS: ");
            UiFormat.Put(destination, ref n, shownRate);

            if (showZoom)
            {
                UiFormat.Put(destination, ref n, "\nZoom: ");
                UiFormat.Put(destination, ref n, zoom, "0.00");
            }

            return n;
        }

        // the selected object's line; returns its length
        private int FormatObject(Span<char> destination, BaseGameObject obj)
        {
            int written = 0;

            if (obj != null && IsMinimized)
            {
                switch (obj)
                {
                    case Mobile mob: destination.TryWrite($"Mobile (0x{mob.Serial:X8})  graphic: 0x{mob.Graphic:X4}  flags: {mob.Flags}  noto: {mob.NotorietyFlag}", out written); break;

                    case Item item: destination.TryWrite($"Item (0x{item.Serial:X8})  graphic: 0x{item.Graphic:X4}  flags: {item.Flags}  amount: {item.Amount} itemdata: {item.ItemData.Flags}", out written); break;

                    case Static st: destination.TryWrite($"Static (0x{st.Graphic:X4})  height: {st.ItemData.Height}  flags: {st.ItemData.Flags}  Alpha: {st.AlphaHue}", out written); break;

                    case Multi multi: destination.TryWrite($"Multi (0x{multi.Graphic:X4})  height: {multi.ItemData.Height}  flags: {multi.ItemData.Flags}", out written); break;

                    case GameEffect: destination.TryWrite($"GameEffect", out written); break;

                    case TextObject overhead: destination.TryWrite($"TextOverhead type: {overhead.Type}  hue: 0x{overhead.Hue:X4}", out written); break;

                    case Land land: destination.TryWrite($"Land (0x{land.Graphic:X4})  flags: {land.TileData.Flags} stretched: {land.IsStretched}  avgZ: {land.AverageZ} minZ: {land.MinZ}", out written); break;
                }
            }

            return written;
        }


        public override void Save(XmlTextWriter writer)
        {
            base.Save(writer);

            writer.WriteAttributeString("minimized", IsMinimized.ToString());
        }

        public override void Restore(XmlElement xml)
        {
            base.Restore(xml);

            bool.TryParse(xml.GetAttribute("minimized"), out bool b);
            IsMinimized = b;
        }

        protected override void OnDragEnd(int x, int y)
        {
            base.OnDragEnd(x, y);
            _last_position.X = ScreenCoordinateX;
            _last_position.Y = ScreenCoordinateY;
        }

        protected override void OnMove(int x, int y)
        {
            base.OnMove(x, y);

            _last_position.X = ScreenCoordinateX;
            _last_position.Y = ScreenCoordinateY;
        }
    }
}
