// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using System;

namespace ClassicUO.Game.UI.Controls
{
    internal class ItemGump : Control
    {
        private ushort _graphic;
        private readonly bool _is_gump;
        private readonly Gump _gump;

        // perf-9: the stack check in AddToRenderLists/Contains runs per frame per item shown; EntityRef repeats the lookup only
        // when the item may have changed
        private EntityRef _item;

        /// <summary>What <c>World.Items.Get(LocalSerial)</c> returns.</summary>
        internal Item ShownItem() => _item.Item(_gump.World, LocalSerial);

        public ItemGump(Gump gump, uint serial, ushort graphic, ushort hue, int x, int y, bool is_gump = false)
        {
            _gump = gump;
            _is_gump = is_gump;

            Reset(serial, graphic, hue, x, y);
        }

        /// <summary>
        /// What the constructor sets; perf-8: also how a container window reuses the control of an item it shows again
        /// (ContainerItemControls), so a reused control holds what a new one would.
        /// </summary>
        internal void Reset(uint serial, ushort graphic, ushort hue, int x, int y)
        {
            AcceptMouseInput = true;
            X = (short)x;
            Y = (short)y;
            HighlightOnMouseOver = true;
            CanPickUp = true;
            LocalSerial = serial;
            WantUpdateSize = false;
            CanMove = false;

            Graphic = graphic;
            Hue = hue;

            // the boxed serial is kept when it is the same (a reused control's)
            if (!(Tooltip is uint shown && shown == serial))
            {
                SetTooltip(serial);
            }
        }

        /// <summary>Tests only: the sprite size and partial-hue flag without a game (false: no texture).</summary>
        internal delegate bool SpriteLookup(ushort graphic, bool isGump, out int width, out int height, out bool partialHue);

        internal static SpriteLookup SpriteForTests;

        public ushort Graphic
        {
            get => _graphic;
            set
            {
                _graphic = value;

                if (SpriteForTests != null)
                {
                    if (!SpriteForTests(value, _is_gump, out int w, out int h, out bool partial))
                    {
                        Dispose();

                        return;
                    }

                    Width = w;
                    Height = h;
                    IsPartialHue = partial;

                    return;
                }

                // perf-6: the size without decoding; the art decodes at first draw or hit test (a worker started on it here)
                if (!TryGetSpriteSize(value, _is_gump, out int width, out int height))
                {
                    Dispose();

                    return;
                }

                // only while something drains finished art (Map.UpdateLoading): otherwise a control disposed before its first
                // draw would leave its worker result holding a pooled buffer and decode budget until teardown
                if (!_is_gump && global::ClassicUO.Game.Map.FirstSightArt.On)
                {
                    Client.Game.UO.Arts.RequestArt(value);
                }

                Width = width;
                Height = height;

                IsPartialHue = !_is_gump && Client.Game.UO.FileManager.TileData.StaticData[value].IsPartialHue;
            }
        }

        /// <summary>
        /// The UV size of the sprite <see cref="AddToRenderLists"/> draws for <paramref name="graphic"/> (static art, or a gump
        /// for chess and backgammon pieces), read from the size tables where possible. False where the lookup would return
        /// no texture.
        /// </summary>
        internal static bool TryGetSpriteSize(ushort graphic, bool isGump, out int width, out int height)
        {
            if (!isGump)
            {
                return Client.Game.UO.Arts.TryGetArtSize(graphic, out width, out height);
            }

            // pack gumps only: a MUL/UOP size can be the archive's while a loose Gumps/<id>.gump overrides the image
            if (Client.Game.UO.FileManager.Gumps.FromPack
                && Client.Game.UO.Gumps.TryGetSize(graphic, out width, out height) && width > 0 && height > 0)
            {
                return true;
            }

            // MUL/UOP gumps (CUO_PACK_GUMPS=0) and gumps without a size: decode, as before
            ref readonly var spriteInfo = ref Client.Game.UO.Gumps.GetGump(graphic);
            width = spriteInfo.UV.Width;
            height = spriteInfo.UV.Height;

            return spriteInfo.Texture != null;
        }

        public ushort Hue { get; set; }
        public bool IsPartialHue { get; set; }
        public bool HighlightOnMouseOver { get; set; }
        public bool CanPickUp { get; set; }

        public override void Update()
        {
            if (IsDisposed)
            {
                return;
            }

            base.Update();

            if (_gump.World.InGame)
            {
                if (
                    CanPickUp
                    && !Client.Game.UO.GameCursor.ItemHold.Enabled
                    && Mouse.LButtonPressed
                    && UIManager.LastControlMouseDown(MouseButtonType.Left) == this
                    && (
                        Mouse.LastLeftButtonClickTime != 0xFFFF_FFFF
                            && Mouse.LastLeftButtonClickTime != 0
                            && Mouse.LastLeftButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK
                                < Time.Ticks
                        || CanPickup()
                    )
                )
                {
                    AttemptPickUp();
                }
                else if (MouseIsOver)
                {
                    SelectedObject.Object = _gump.World.Get(LocalSerial);
                }
            }
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (IsDisposed)
            {
                return false;
            }

            base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
            float layerDepth = layerDepthRef;

            bool partialHue = IsPartialHue;
            ushort hue = Hue;

            if (HighlightOnMouseOver && MouseIsOver || LocalSerial == SelectedObject.SelectedContainer?.Serial)
            {
                hue = 0x0035;
                partialHue = false;
            }

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(hue, partialHue, 1);

            ref readonly var spriteInfo = ref _is_gump
                ? ref Client.Game.UO.Gumps.GetGump(Graphic)
                : ref Client.Game.UO.Arts.GetArt(Graphic);

            var texture = spriteInfo.Texture;
            if (spriteInfo.Texture != null)
            {
                Rectangle rect = new Rectangle(x, y, Width, Height);


                var sourceRectangle = spriteInfo.UV;
                renderLists.AddGumpWithAtlas(GumpCommand.Sprite(texture, rect, sourceRectangle, hueVector, layerDepth));

                Item item = ShownItem();

                if (
                    item != null
                    && !item.IsMulti
                    && !item.IsCoin
                    && item.Amount > 1
                    && item.ItemData.IsStackable
                )
                {
                    rect.X += 5;
                    rect.Y += 5;

                    renderLists.AddGumpWithAtlas(GumpCommand.Sprite(texture, rect, sourceRectangle, hueVector, layerDepth));
                }
            }

            return true;
        }

        public override bool Contains(int x, int y)
        {
            ref readonly var spriteInfo = ref _is_gump
                ? ref Client.Game.UO.Gumps.GetGump(Graphic)
                : ref Client.Game.UO.Arts.GetArt(Graphic);

            if (spriteInfo.Texture == null)
            {
                return false;
            }

            x -= Offset.X;
            y -= Offset.Y;

            if (
                ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.ScaleItemsInsideContainers
            )
            {
                float scale = UIManager.ContainerScale;

                x = (int)(x / scale);
                y = (int)(y / scale);
            }

            if (_is_gump)
            {
                if (Client.Game.UO.Gumps.PixelCheck(Graphic, x, y))
                {
                    return true;
                }

                Item item = ShownItem();

                if (item != null && !item.IsCoin && item.Amount > 1 && item.ItemData.IsStackable)
                {
                    if (Client.Game.UO.Gumps.PixelCheck(Graphic, x - 5, y - 5))
                    {
                        return true;
                    }
                }
            }
            else
            {
                if (Client.Game.UO.Arts.PixelCheck(Graphic, x, y))
                {
                    return true;
                }

                Item item = ShownItem();

                if (item != null && !item.IsCoin && item.Amount > 1 && item.ItemData.IsStackable)
                {
                    if (Client.Game.UO.Arts.PixelCheck(Graphic, x - 5, y - 5))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            SelectedObject.Object = _gump.World.Get(LocalSerial);
            base.OnMouseUp(x, y, button);
        }

        protected override void OnMouseOver(int x, int y)
        {
            SelectedObject.Object = _gump.World.Get(LocalSerial);
        }

        private bool CanPickup()
        {
            Point offset = Mouse.LDragOffset;

            if (
                Math.Abs(offset.X) < Constants.MIN_PICKUP_DRAG_DISTANCE_PIXELS
                && Math.Abs(offset.Y) < Constants.MIN_PICKUP_DRAG_DISTANCE_PIXELS
            )
            {
                return false;
            }

            SplitMenuGump split = UIManager.GetGump<SplitMenuGump>(LocalSerial);

            if (split == null)
            {
                return true;
            }

            split.X = Mouse.LClickPosition.X - 80;
            split.Y = Mouse.LClickPosition.Y - 40;
            UIManager.AttemptDragControl(split, true);
            split.BringOnTop();

            return false;
        }

        protected override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            if (button != MouseButtonType.Left || _gump.World.TargetManager.IsTargeting)
            {
                return false;
            }

            Item item = _gump.World.Items.Get(LocalSerial);
            Item container;

            if (
                !Keyboard.Ctrl
                && ProfileManager.CurrentProfile.DoubleClickToLootInsideContainers
                && item != null
                && !item.IsDestroyed
                && !item.ItemData.IsContainer
                && !_gump.World.State.HasChildren(item)
                && (container = _gump.World.Items.Get(item.RootContainer)) != null
                && container != _gump.World.Player.FindItemByLayer(Layer.Backpack)
            )
            {
                GameActions.GrabItem(_gump.World, LocalSerial, item.Amount);
            }
            else
            {
                GameActions.DoubleClick(_gump.World, LocalSerial);
            }

            return true;
        }

        private void AttemptPickUp()
        {
            if (CanPickUp)
            {
                ref readonly var spriteInfo = ref _is_gump
                    ? ref Client.Game.UO.Gumps.GetGump(Graphic)
                    : ref Client.Game.UO.Arts.GetArt(Graphic);

                int centerX = spriteInfo.UV.Width >> 1;
                int centerY = spriteInfo.UV.Height >> 1;

                if (
                    ProfileManager.CurrentProfile != null
                    && ProfileManager.CurrentProfile.ScaleItemsInsideContainers
                )
                {
                    float scale = UIManager.ContainerScale;
                    centerX = (int)(centerX * scale);
                    centerY = (int)(centerY * scale);
                }

                if (
                    ProfileManager.CurrentProfile != null
                    && ProfileManager.CurrentProfile.RelativeDragAndDropItems
                )
                {
                    Point p = new Point(
                        centerX - (Mouse.Position.X - ScreenCoordinateX),
                        centerY - (Mouse.Position.Y - ScreenCoordinateY)
                    );

                    GameActions.PickUp(_gump.World, LocalSerial, centerX, centerY, offset: p, is_gump: _is_gump);
                }
                else
                {
                    GameActions.PickUp(_gump.World, LocalSerial, centerX, centerY, is_gump: _is_gump);
                }
            }
        }
    }
}
