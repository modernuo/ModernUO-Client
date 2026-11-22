// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.UI.Gumps
{
    internal class WorldViewportGump : Gump
    {
        public const int BORDER_WIDTH = 5;
        private readonly BorderControl _borderControl;
        private readonly Button _button;
        private bool _clicked;
        private Point _lastSize,
            _savedSize;
        private readonly GameScene _scene;
        private readonly SystemChatControl _systemChatControl;

        public WorldViewportGump(World world, GameScene scene) : base(world, 0, 0)
        {
            _scene = scene;
            AcceptMouseInput = false;
            CanMove = !ProfileManager.CurrentProfile.GameWindowLock;
            CanCloseWithEsc = false;
            CanCloseWithRightClick = false;
            LayerOrder = UILayer.Under;
            X = scene.Camera.Bounds.X - BORDER_WIDTH;
            Y = scene.Camera.Bounds.Y - BORDER_WIDTH;
            _savedSize = _lastSize = new Point(
                scene.Camera.Bounds.Width,
                scene.Camera.Bounds.Height
            );

            _button = new Button(0, 0x837, 0x838, 0x838);

            _button.MouseDown += (sender, e) =>
            {
                if (!ProfileManager.CurrentProfile.GameWindowLock)
                {
                    _clicked = true;
                }
            };

            _button.MouseUp += (sender, e) =>
            {
                if (!ProfileManager.CurrentProfile.GameWindowLock)
                {
                    Point n = ResizeViewport(_lastSize);

                    UIManager.GetGump<OptionsGump>()?.UpdateVideo();

                    if (Client.Game.UO.Version >= ClientVersion.CV_200)
                    {
                        NetClient.Socket.Send_GameWindowSize((uint)n.X, (uint)n.Y);
                    }

                    _clicked = false;
                }
            };

            _button.SetTooltip(ResGumps.ResizeGameWindow);
            Width = scene.Camera.Bounds.Width + BORDER_WIDTH * 2;
            Height = scene.Camera.Bounds.Height + BORDER_WIDTH * 2;

            _borderControl = new BorderControl(0, 0, Width, Height, 4);

            _borderControl.DragEnd += (sender, e) =>
            {
                UIManager.GetGump<OptionsGump>()?.UpdateVideo();
            };

            UIManager.SystemChat = _systemChatControl = new SystemChatControl(
                this,
                BORDER_WIDTH,
                BORDER_WIDTH,
                scene.Camera.Bounds.Width,
                scene.Camera.Bounds.Height
            );

            Add(_borderControl);
            Add(_button);
            Add(_systemChatControl);
            Resize();
        }

        public override void Update()
        {
            base.Update();

            if (IsDisposed)
            {
                return;
            }

            // a window shrink or scale change can leave the viewport outside the new UI bounds: keep it grabbable
            KeepOnScreen();

            if (Mouse.IsDragging)
            {
                Point offset = Mouse.LDragOffset;

                _lastSize = _savedSize;

                if (_clicked && offset != Point.Zero)
                {
                    int w = _lastSize.X + offset.X;
                    int h = _lastSize.Y + offset.Y;

                    int targetWidth = 640;
                    int targetHeight = 480;

                    if (w < targetWidth)
                    {
                        w = targetWidth;
                    }

                    if (h < targetHeight)
                    {
                        h = targetHeight;
                    }

                    if (w > Client.Game.ClientBounds.Width - BORDER_WIDTH)
                    {
                        w = Client.Game.ClientBounds.Width - BORDER_WIDTH;
                    }

                    if (h > Client.Game.ClientBounds.Height - BORDER_WIDTH)
                    {
                        h = Client.Game.ClientBounds.Height - BORDER_WIDTH;
                    }

                    _lastSize.X = w;
                    _lastSize.Y = h;
                }

                if (
                    _scene.Camera.Bounds.Width != _lastSize.X
                    || _scene.Camera.Bounds.Height != _lastSize.Y
                )
                {
                    Width = _lastSize.X + BORDER_WIDTH * 2;
                    Height = _lastSize.Y + BORDER_WIDTH * 2;
                    _scene.Camera.Bounds.Width = _lastSize.X;
                    _scene.Camera.Bounds.Height = _lastSize.Y;

                    Resize();
                }
            }
        }

        protected override void OnDragEnd(int x, int y)
        {
            base.OnDragEnd(x, y);

            KeepOnScreen();

            UIManager.GetGump<OptionsGump>()?.UpdateVideo();
            UpdateGameWindowPos();
        }

        protected override void OnMove(int x, int y)
        {
            base.OnMove(x, y);

            KeepOnScreen();
            UpdateGameWindowPos();
        }

        /// <summary>
        /// The viewport may sit partly outside the window (its world composite is clipped there), but at least
        /// RenderGeometry.ViewportKeepVisible UI units stay inside on every side so it can be dragged back; the camera bounds
        /// follow the gump.
        /// </summary>
        private void KeepOnScreen()
        {
            Rectangle client = Client.Game.ClientBounds;
            Point p = RenderGeometry.ClampPlacement(Location, Width, Height, client.Width, client.Height);

            if (p != Location)
            {
                Location = p;
            }

            int cx = ScreenCoordinateX + BORDER_WIDTH, cy = ScreenCoordinateY + BORDER_WIDTH;

            if (_scene.Camera.Bounds.X != cx || _scene.Camera.Bounds.Y != cy)
            {
                _scene.Camera.Bounds.X = cx;
                _scene.Camera.Bounds.Y = cy;
                UpdateGameWindowPos();
            }
        }

        private void UpdateGameWindowPos()
        {
            if (_scene != null)
            {
                _scene.UpdateDrawPosition = true;
            }
        }

        private void Resize()
        {
            _borderControl.Width = Width;
            _borderControl.Height = Height;
            _button.X = Width - (_button.Width >> 1);
            _button.Y = Height - (_button.Height >> 1);
            _scene.Camera.Bounds.Width = _systemChatControl.Width = Width - BORDER_WIDTH * 2;
            _scene.Camera.Bounds.Height = _systemChatControl.Height = Height - BORDER_WIDTH * 2;
            _systemChatControl.Resize();
            WantUpdateSize = true;

            UpdateGameWindowPos();
        }

        public void SetGameWindowPosition(Point pos)
        {
            Location = pos;
            KeepOnScreen();
            UpdateGameWindowPos();
        }

        /// <summary>Sizes the viewport to a window size in back-buffer pixels (the full-size viewport): the same UI units the
        /// UI target has (RenderGeometry.UiUnits), at least 640x480.</summary>
        public Point ResizeGameWindow(Point newSize)
        {
            float scale = Client.Game.DpiScale;

            return ResizeViewport(new Point(RenderGeometry.UiUnits(newSize.X, scale), RenderGeometry.UiUnits(newSize.Y, scale)));
        }

        /// <summary>Sizes the viewport in UI units, at least 640x480; returns the size set.</summary>
        public Point ResizeViewport(Point newSize)
        {
            int targetWidth = 640;
            int targetHeight = 480;
            if (newSize.X < targetWidth)
            {
                newSize.X = targetWidth;
            }

            if (newSize.Y < targetHeight)
            {
                newSize.Y = targetHeight;
            }

            _lastSize = _savedSize = newSize;

            if (
                _scene.Camera.Bounds.Width != _lastSize.X
                || _scene.Camera.Bounds.Height != _lastSize.Y
            )
            {
                _scene.Camera.Bounds.Width = _lastSize.X;
                _scene.Camera.Bounds.Height = _lastSize.Y;
                Width = _scene.Camera.Bounds.Width + BORDER_WIDTH * 2;
                Height = _scene.Camera.Bounds.Height + BORDER_WIDTH * 2;

                Resize();
            }

            return newSize;
        }

        /// <summary>Sets the viewport's size in UI units without the 640x480 floor (driver geometry checks).</summary>
        public void SetViewportSize(Point size)
        {
            _lastSize = _savedSize = size;
            _scene.Camera.Bounds.Width = size.X;
            _scene.Camera.Bounds.Height = size.Y;
            Width = size.X + BORDER_WIDTH * 2;
            Height = size.Y + BORDER_WIDTH * 2;
            Resize();
        }

        public override bool Contains(int x, int y)
        {
            if (
                x >= BORDER_WIDTH
                && x < Width - BORDER_WIDTH * 2
                && y >= BORDER_WIDTH
                && y
                    < Height
                        - BORDER_WIDTH * 2
                        - (
                            _systemChatControl?.TextBoxControl != null
                            && _systemChatControl.IsActive
                                ? _systemChatControl.TextBoxControl.Height
                                : 0
                        )
            )
            {
                return false;
            }

            return base.Contains(x, y);
        }
    }

    internal class BorderControl : Control
    {
        public int BorderSize { get; internal set; }

        const ushort H_BORDER = 0x0A8C;
        const ushort V_BORDER = 0x0A8D;

        public BorderControl(int x, int y, int w, int h, int borderSize)
        {
            X = x;
            Y = y;
            Width = w;
            Height = h;
            BorderSize = borderSize;
            CanMove = true;
            AcceptMouseInput = true;
        }

        public ushort Hue { get; set; }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            if (Hue != 0)
            {
                hueVector.X = Hue;
                hueVector.Y = 1;
            }
            float layerDepth = layerDepthRef;

            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(H_BORDER);

            // sopra
            renderLists.AddGumpWithAtlas(GumpCommand.Tiled(gumpInfo.Texture, new Rectangle(x, y, Width, BorderSize), gumpInfo.UV, hueVector, layerDepth));

            // sotto
            renderLists.AddGumpWithAtlas(GumpCommand.Tiled(gumpInfo.Texture, new Rectangle(x, y + Height - BorderSize, Width, BorderSize), gumpInfo.UV, hueVector, layerDepth));

            gumpInfo = ref Client.Game.UO.Gumps.GetGump(V_BORDER);
            //sx
            renderLists.AddGumpWithAtlas(GumpCommand.Tiled(gumpInfo.Texture, new Rectangle(x, y, BorderSize, Height), gumpInfo.UV, hueVector, layerDepth));

            //dx
            renderLists.AddGumpWithAtlas
            (
                GumpCommand.Tiled
                (
                    gumpInfo.Texture,
                    new Rectangle(x + Width - BorderSize, y + (gumpInfo.UV.Width >> 1), BorderSize, Height - BorderSize),
                    gumpInfo.UV,
                    hueVector,
                    layerDepth
                )
            );

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }
    }
}
