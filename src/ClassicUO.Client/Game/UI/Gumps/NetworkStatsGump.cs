// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using System;
using System.Xml;
using Uo.Buffers;

namespace ClassicUO.Game.UI.Gumps
{
    internal class NetworkStatsGump : Gump, IGumpDeferredDraw
    {
        private static Point _last_position = new Point(-1, -1);

        private uint _ping, _deltaBytesReceived, _deltaBytesSent;
        private uint _time_to_update;
        private readonly AlphaBlendControl _trans;
        // G2: the shown text lives in this buffer and is drawn from it; a refresh that formats the same text does nothing
        private readonly char[] _text = new char[TEXT_CAPACITY];
        private int _textLength;
        private const int TEXT_CAPACITY = 128;


        public NetworkStatsGump(World world, int x, int y) : base(world, 0, 0)
        {
            CanMove = true;
            CanCloseWithEsc = false;
            CanCloseWithRightClick = true;
            AcceptMouseInput = true;
            AcceptKeyboardInput = false;

            _ping = _deltaBytesReceived = _deltaBytesSent = 0;

            X = _last_position.X <= 0 ? x : _last_position.X;
            Y = _last_position.Y <= 0 ? y : _last_position.Y;
            Width = 100;
            Height = 30;

            Add
            (
                _trans = new AlphaBlendControl(.7f)
                {
                    Width = Width,
                    Height = Height
                }
            );


            LayerOrder = UILayer.Over;

            WantUpdateSize = false;
        }

        public override GumpType GumpType => GumpType.NetStats;

        public bool IsMinimized { get; set; }

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

            if (Time.Ticks > _time_to_update)
            {
                _time_to_update = Time.Ticks + 100;

                if (NetClient.Socket.IsConnected)
                {
                    _ping = NetClient.Socket.Statistics.Ping;
                    _deltaBytesReceived = NetClient.Socket.Statistics.DeltaBytesReceived;
                    _deltaBytesSent = NetClient.Socket.Statistics.DeltaBytesSent;
                }

                Span<char> span = stackalloc char[TEXT_CAPACITY];
                int length = FormatText(span, IsMinimized, _ping, _deltaBytesReceived, _deltaBytesSent);

                if (!span.Slice(0, length).SequenceEqual(_text.AsSpan(0, _textLength)))
                {
                    span.Slice(0, length).CopyTo(_text);
                    _textLength = length;

                    Vector2 size = Fonts.Bold.MeasureString(_text.AsSpan(0, _textLength));

                    _trans.Width = Width = (int) (size.X + 20);
                    _trans.Height = Height = (int) (size.Y + 20);
                    WantUpdateSize = true;
                }
            }

            // visual-due tracking: the next timed change
            ClassicUO.Loop.VisualDue.At(_time_to_update, ClassicUO.Loop.VisualSource.Gump);
        }

        // the shown text: "Ping: N ms", plus the received and sent rates when expanded; returns its length
        internal static int FormatText(Span<char> destination, bool minimized, uint ping, uint received, uint sent)
        {
            int n = 0;
            UiFormat.Put(destination, ref n, "Ping: ");
            UiFormat.Put(destination, ref n, ping);
            UiFormat.Put(destination, ref n, " ms");

            if (minimized)
            {
                return n;
            }

            UiFormat.Put(destination, ref n, "\nIn: ");
            int start = n;
            NetStatistics.FormatSizeAdaptive(received, destination.Slice(n), out int written);
            n += written;
            UiFormat.PadRight(destination, ref n, start, 6);
            UiFormat.Put(destination, ref n, " Out: ");
            start = n;
            NetStatistics.FormatSizeAdaptive(sent, destination.Slice(n), out written);
            n += written;
            UiFormat.PadRight(destination, ref n, start, 6);

            return n;
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

            if (_ping < 150)
            {
                hueVector.X = 0x44; // green
            }
            else if (_ping < 200)
            {
                hueVector.X = 0x34; // yellow
            }
            else if (_ping < 300)
            {
                hueVector.X = 0x31; // orange
            }
            else
            {
                hueVector.X = 0x20; // red
            }

            hueVector.Y = 1;

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