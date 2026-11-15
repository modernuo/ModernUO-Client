// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace ClassicUO.Game.UI.Gumps
{
    internal class NameOverheadGump : Gump, IGumpDeferredDraw
    {
        // GumpCommand.Deferred args: the name text and the hp bar draw at the gump layer flush, as the closures they replace
        private const int DRAW_TEXT = 0;
        private const int DRAW_HP_BAR = 1;

        private AlphaBlendControl _background;
        private Point _lockedPosition,
            _lastLeftMousePositionDown;
        private bool _positionLocked,
            _leftMouseIsDown;
        private readonly RenderedText _renderedText;
        private Texture2D _borderColor = SolidColorTextureCache.GetTexture(Color.Black);
        private bool _showHpBar;
        private int _hpBarWidth,
            _hpBarFilled;
        private Color _hpBarColor;
        private string _fallbackName,
            _fitSource;
        private ushort _fallbackGraphic,
            _fallbackAmount;
        private bool _fitHtml;
        private int _fallbackClilocRevision;

        public NameOverheadGump(World world, uint serial) : base(world, serial, 0)
        {
            CanMove = false;
            AcceptMouseInput = true;
            CanCloseWithRightClick = true;

            Entity entity = World.Get(serial);

            if (entity == null)
            {
                Dispose();

                return;
            }

            _renderedText = RenderedText.Create(
                string.Empty,
                entity is Mobile m ? Notoriety.GetHue(m.NotorietyFlag) : (ushort)0x0481,
                0xFF,
                true,
                FontStyle.BlackBorder,
                TEXT_ALIGN_TYPE.TS_CENTER,
                100,
                30,
                true
            );

            SetTooltip(entity);

            BuildGump();
        }

        public bool SetName()
        {
            Entity entity = World.Get(LocalSerial);

            if (entity == null)
            {
                return false;
            }

            if (entity is Item item)
            {
                if (!World.OPL.TryGetNameAndData(item, out string t, out _))
                {
                    t = FallbackItemName(item);
                }

                if (string.IsNullOrEmpty(t))
                {
                    return false;
                }

                FitName(t, true);

                Width = _background.Width = Constants.OBJECT_HANDLES_GUMP_WIDTH + 4;
                _showHpBar = false;
                Height = _background.Height = Constants.OBJECT_HANDLES_GUMP_HEIGHT + 4;

                WantUpdateSize = false;

                return true;
            }

            if (!string.IsNullOrEmpty(entity.Name))
            {
                FitName(entity.Name, false);

                Width = _background.Width = Constants.OBJECT_HANDLES_GUMP_WIDTH + 4;
                _showHpBar = entity is Mobile && ProfileManager.CurrentProfile.NameOverheadShowHpBar;
                int hpBarExtra = _showHpBar ? Constants.OBJECT_HANDLES_HP_BAR_HEIGHT + 1 : 0;
                Height = _background.Height = Constants.OBJECT_HANDLES_GUMP_HEIGHT + 4 + hpBarExtra;

                WantUpdateSize = false;

                return true;
            }

            return false;
        }

        // SetName runs every frame the gump is queued: the name an item without properties gets is built again only when its
        // graphic or amount changes, or cliloc is reloaded (Options, "Reload Our Assets", or a language change)
        private string FallbackItemName(Item item)
        {
            ushort graphic = item.Graphic;
            ushort amount = item.Amount;
            var clilocs = Client.Game.UO.FileManager.Clilocs;
            int revision = clilocs.Revision;

            if (_fallbackName != null && _fallbackGraphic == graphic && _fallbackAmount == amount && _fallbackClilocRevision == revision)
            {
                return _fallbackName;
            }

            string t = null;

            if (!item.IsCorpse && amount > 1)
            {
                t = amount.ToString() + ' ';
            }

            if (string.IsNullOrEmpty(item.ItemData.Name))
            {
                t += clilocs.GetString(1020000 + graphic, true, t);
            }
            else
            {
                t += StringHelper.CapitalizeAllWords(StringHelper.GetPluralAdjustedString(item.ItemData.Name, amount > 1));
            }

            _fallbackName = t;
            _fallbackGraphic = graphic;
            _fallbackAmount = amount;
            _fallbackClilocRevision = revision;

            return t;
        }

        // the width fit (a measure and, for a long name, a new cut string) runs again only when the name's text changes: a
        // name or status packet can hand over a new string with the same text
        private void FitName(string t, bool html)
        {
            if (string.Equals(t, _fitSource, StringComparison.Ordinal) && html == _fitHtml)
            {
                return;
            }

            _fitSource = t;
            _fitHtml = html;

            var fonts = Client.Game.UO.FileManager.Fonts;

            if (html)
            {
                fonts.SetUseHTML(true);
                fonts.RecalculateWidthByInfo = true;
            }

            int width = fonts.GetWidthUnicode(_renderedText.Font, t);

            if (width > Constants.OBJECT_HANDLES_GUMP_WIDTH)
            {
                t = fonts.GetTextByWidthUnicode(
                    _renderedText.Font,
                    t.AsSpan(),
                    Constants.OBJECT_HANDLES_GUMP_WIDTH,
                    true,
                    TEXT_ALIGN_TYPE.TS_CENTER,
                    (ushort)FontStyle.BlackBorder
                );

                width = Constants.OBJECT_HANDLES_GUMP_WIDTH;
            }

            _renderedText.MaxWidth = width;
            _renderedText.Text = t;

            if (html)
            {
                fonts.RecalculateWidthByInfo = false;
                fonts.SetUseHTML(false);
            }
        }

        private void BuildGump()
        {
            Entity entity = World.Get(LocalSerial);

            if (entity == null)
            {
                Dispose();

                return;
            }

            Add(
                _background = new AlphaBlendControl(.7f)
                {
                    WantUpdateSize = false,
                    Hue = entity is Mobile m ? Notoriety.GetHue(m.NotorietyFlag) : (ushort)0x0481
                }
            );
        }

        protected override void CloseWithRightClick()
        {
            Entity entity = World.Get(LocalSerial);

            if (entity != null)
            {
                entity.ObjectHandlesStatus = ObjectHandlesStatus.CLOSED;

                if (entity is Item)
                {
                    // pa-gpusig: handle state the Gpu item cache reads, changed outside the walk
                    Scenes.GpuItemGen.Touch(entity);
                }
            }

            base.CloseWithRightClick();
        }

        private void DoDrag()
        {
            var delta = Mouse.Position - _lastLeftMousePositionDown;

            if (
                Math.Abs(delta.X) <= Constants.MIN_GUMP_DRAG_DISTANCE
                && Math.Abs(delta.Y) <= Constants.MIN_GUMP_DRAG_DISTANCE
            )
            {
                return;
            }

            _leftMouseIsDown = false;
            _positionLocked = false;

            Entity entity = World.Get(LocalSerial);

            if (entity is Mobile || entity is Item it && it.IsDamageable)
            {
                if (UIManager.IsDragging)
                {
                    return;
                }

                BaseHealthBarGump gump = UIManager.GetGump<BaseHealthBarGump>(LocalSerial);
                gump?.Dispose();

                if (entity == World.Player && ProfileManager.CurrentProfile.StatusGumpBarMutuallyExclusive)
                {
                    StatusGumpBase.GetStatusGump()?.Dispose();
                }

                if (ProfileManager.CurrentProfile.CustomBarsToggled)
                {
                    Rectangle rect = new Rectangle(
                        0,
                        0,
                        HealthBarGumpCustom.HPB_WIDTH,
                        HealthBarGumpCustom.HPB_HEIGHT_SINGLELINE
                    );

                    UIManager.Add(
                        gump = new HealthBarGumpCustom(World, entity)
                        {
                            X = Mouse.Position.X - (rect.Width >> 1),
                            Y = Mouse.Position.Y - (rect.Height >> 1)
                        }
                    );
                }
                else
                {
                    ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(0x0804);

                    UIManager.Add(
                        gump = new HealthBarGump(World, entity)
                        {
                            X = Mouse.LClickPosition.X - (gumpInfo.UV.Width >> 1),
                            Y = Mouse.LClickPosition.Y - (gumpInfo.UV.Height >> 1)
                        }
                    );
                }

                UIManager.AttemptDragControl(gump, true);
            }
            else if (entity != null)
            {
                GameActions.PickUp(World, LocalSerial, 0, 0);

                //if (entity.Texture != null)
                //    GameActions.PickUp(LocalSerial, entity.Texture.Width >> 1, entity.Texture.Height >> 1);
                //else
                //    GameActions.PickUp(LocalSerial, 0, 0);
            }
        }

        protected override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left)
            {
                if (SerialHelper.IsMobile(LocalSerial))
                {
                    if (World.Player.InWarMode)
                    {
                        GameActions.Attack(World, LocalSerial);
                    }
                    else
                    {
                        GameActions.DoubleClick(World, LocalSerial);
                    }
                }
                else
                {
                    if (!GameActions.OpenCorpse(World, LocalSerial))
                    {
                        GameActions.DoubleClick(World, LocalSerial);
                    }
                }

                return true;
            }

            return false;
        }

        protected override void OnMouseDown(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left)
            {
                _lastLeftMousePositionDown = Mouse.Position;
                _leftMouseIsDown = true;
            }

            base.OnMouseDown(x, y, button);
        }

        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left)
            {
                _leftMouseIsDown = false;

                if (!Client.Game.UO.GameCursor.ItemHold.Enabled)
                {
                    if (
                        UIManager.IsDragging
                        || Math.Max(Math.Abs(Mouse.LDragOffset.X), Math.Abs(Mouse.LDragOffset.Y))
                            >= 1
                    )
                    {
                        _positionLocked = false;

                        return;
                    }
                }

                if (World.TargetManager.IsTargeting)
                {
                    switch (World.TargetManager.TargetingState)
                    {
                        case CursorTarget.Position:
                        case CursorTarget.Object:
                        case CursorTarget.Grab:
                        case CursorTarget.SetGrabBag:
                        case CursorTarget.CallbackTarget:
                            World.TargetManager.Target(LocalSerial);
                            Mouse.LastLeftButtonClickTime = 0;

                            break;

                        case CursorTarget.SetTargetClientSide:
                            World.TargetManager.Target(LocalSerial);
                            Mouse.LastLeftButtonClickTime = 0;
                            UIManager.Add(new InspectorGump(World, World.Get(LocalSerial)));

                            break;

                        case CursorTarget.HueCommandTarget:
                            World.CommandManager.OnHueTarget(World.Get(LocalSerial));

                            break;
                    }
                }
                else
                {
                    if (
                        Client.Game.UO.GameCursor.ItemHold.Enabled
                        && !Client.Game.UO.GameCursor.ItemHold.IsFixedPosition
                    )
                    {
                        uint drop_container = 0xFFFF_FFFF;
                        bool can_drop = false;
                        ushort dropX = 0;
                        ushort dropY = 0;
                        sbyte dropZ = 0;

                        Entity obj = World.Get(LocalSerial);

                        if (obj != null)
                        {
                            can_drop = obj.Distance <= Constants.DRAG_ITEMS_DISTANCE;

                            if (can_drop)
                            {
                                if (obj is Item it && it.ItemData.IsContainer || obj is Mobile)
                                {
                                    dropX = 0xFFFF;
                                    dropY = 0xFFFF;
                                    dropZ = 0;
                                    drop_container = obj.Serial;
                                }
                                else if (
                                    obj is Item it2
                                    && (
                                        it2.ItemData.IsSurface
                                        || it2.ItemData.IsStackable
                                            && it2.DisplayedGraphic
                                                == Client.Game.UO.GameCursor.ItemHold.DisplayedGraphic
                                    )
                                )
                                {
                                    dropX = obj.X;
                                    dropY = obj.Y;
                                    dropZ = obj.Z;

                                    if (it2.ItemData.IsSurface)
                                    {
                                        dropZ += (sbyte)(
                                            it2.ItemData.Height == 0xFF ? 0 : it2.ItemData.Height
                                        );
                                    }
                                    else
                                    {
                                        drop_container = obj.Serial;
                                    }
                                }
                            }
                            else
                            {
                                Client.Game.Audio.PlaySound(0x0051);
                            }

                            if (can_drop)
                            {
                                if (drop_container == 0xFFFF_FFFF && dropX == 0 && dropY == 0)
                                {
                                    can_drop = false;
                                }

                                if (can_drop)
                                {
                                    GameActions.DropItem(
                                        Client.Game.UO.GameCursor.ItemHold.Serial,
                                        dropX,
                                        dropY,
                                        dropZ,
                                        drop_container
                                    );
                                }
                            }
                        }
                    }
                    else if (!World.DelayedObjectClickManager.IsEnabled)
                    {
                        World.DelayedObjectClickManager.Set(
                            LocalSerial,
                            Mouse.Position.X,
                            Mouse.Position.Y,
                            Time.Ticks + Mouse.MOUSE_DELAY_DOUBLE_CLICK
                        );
                    }
                }
            }

            base.OnMouseUp(x, y, button);
        }

        protected override void OnMouseOver(int x, int y)
        {
            if (_leftMouseIsDown)
            {
                DoDrag();
            }

            if (!_positionLocked && SerialHelper.IsMobile(LocalSerial))
            {
                Mobile m = World.FindMobile(LocalSerial);

                if (m == null)
                {
                    Dispose();

                    return;
                }

                _positionLocked = true;

                Client.Game.UO.Animations.GetAnimationDimensions(
                    m.AnimIndex,
                    m.GetGraphicForAnimation(),
                    /*(byte) m.GetDirectionForAnimation()*/
                    0,
                    /*Mobile.GetGroupForAnimation(m, isParent:true)*/
                    0,
                    m.IsMounted,
                    /*(byte) m.AnimIndex*/
                    0,
                    out int centerX,
                    out int centerY,
                    out int width,
                    out int height
                );

                Vector3 offset = m.Offset;
                _lockedPosition.X = (int)(m.RealScreenPosition.X + offset.X + 22 + 5);

                _lockedPosition.Y = (int)(
                    m.RealScreenPosition.Y
                    + (offset.Y - offset.Z)
                    - (height + centerY + 8 + 10)
                    + (
                        m.IsGargoyle && m.IsFlying
                            ? -22
                            : !m.IsMounted
                                ? 22
                                : 0
                    )
                );
            }

            base.OnMouseOver(x, y);
        }

        protected override void OnMouseExit(int x, int y)
        {
            _positionLocked = false;
            base.OnMouseExit(x, y);
        }

        public override void Update()
        {
            base.Update();

            Entity entity = World.Get(LocalSerial);

            if (
                entity == null
                || entity.IsDestroyed
                || entity.ObjectHandlesStatus == ObjectHandlesStatus.NONE
                || entity.ObjectHandlesStatus == ObjectHandlesStatus.CLOSED
            )
            {
                Dispose();
            }
            else
            {
                if (entity == World.TargetManager.LastTargetInfo.Serial)
                {
                    _borderColor = SolidColorTextureCache.GetTexture(Color.Red);
                    _background.Hue = _renderedText.Hue = entity is Mobile m
                        ? Notoriety.GetHue(m.NotorietyFlag)
                        : (ushort)0x0481;
                }
                else
                {
                    _borderColor = SolidColorTextureCache.GetTexture(Color.Black);
                    _background.Hue = _renderedText.Hue = entity is Mobile m
                        ? Notoriety.GetHue(m.NotorietyFlag)
                        : (ushort)0x0481;
                }
            }
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (IsDisposed || !SetName())
            {
                return false;
            }

            if (SerialHelper.IsMobile(LocalSerial))
            {
                Mobile m = World.FindMobile(LocalSerial);

                if (m == null)
                {
                    Dispose();

                    return false;
                }

                if (_positionLocked)
                {
                    x = _lockedPosition.X;
                    y = _lockedPosition.Y;
                }
                else
                {
                    Client.Game.UO.Animations.GetAnimationDimensions(
                        m.AnimIndex,
                        m.GetGraphicForAnimation(),
                        /*(byte) m.GetDirectionForAnimation()*/
                        0,
                        /*Mobile.GetGroupForAnimation(m, isParent:true)*/
                        0,
                        m.IsMounted,
                        /*(byte) m.AnimIndex*/
                        0,
                        out int centerX,
                        out int centerY,
                        out int width,
                        out int height
                    );

                    Vector3 offset = m.Offset;
                    x = (int)(m.RealScreenPosition.X + offset.X + 22 + 5);
                    y = (int)(
                        m.RealScreenPosition.Y
                        + (offset.Y - offset.Z)
                        - (height + centerY + 8 + 10)
                        + (
                            m.IsGargoyle && m.IsFlying
                                ? -22
                                : !m.IsMounted
                                    ? 22
                                    : 0
                        )
                    );
                }
            }
            else if (SerialHelper.IsItem(LocalSerial))
            {
                Item item = World.FindItem(LocalSerial);

                if (item == null)
                {
                    Dispose();

                    return false;
                }

                var bounds = Client.Game.UO.Arts.GetRealArtBounds(item.Graphic);

                Vector3 offset = ItemOffsets.Read(item);
                x = item.RealScreenPosition.X + (int)offset.X + 22 + 5;
                y =
                    item.RealScreenPosition.Y
                    + (int)(offset.Y - offset.Z)
                    + (bounds.Height >> 1);
            }

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            Point p = Client.Game.Scene.Camera.WorldToScreen(new Point(x, y));
            x = p.X - (Width >> 1);
            y = p.Y - (Height >> 1);

            var camera = Client.Game.Scene.Camera;
            x += camera.Bounds.X;
            y += camera.Bounds.Y;

            if (x < camera.Bounds.X || x + Width > camera.Bounds.Right)
            {
                return false;
            }

            if (y < camera.Bounds.Y || y + Height > camera.Bounds.Bottom)
            {
                return false;
            }
            float layerDepth = layerDepthRef;

            X = x;
            Y = y;
            renderLists.AddGumpNoAtlas(GumpCommand.Outline(_borderColor, x - 1, y - 1, Width + 1, Height + 1, hueVector, layerDepth));

            base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);

            int renderedTextOffset = Math.Max(0, Width - _renderedText.Width - 4) >> 1;
            renderLists.AddGumpNoAtlas(GumpCommand.Deferred(this, x + 2 + renderedTextOffset, y + 2, layerDepth + CHILD_LAYER_INCREMENT * 2, DRAW_TEXT));

            if (_showHpBar && World.Get(LocalSerial) is Mobile mob)
            {
                _hpBarWidth = Width;
                _hpBarFilled = _hpBarWidth * mob.HitsPercentage / 100;
                _hpBarColor = HpBarColor(mob.HitsPercentage);

                renderLists.AddGumpNoAtlas(
                    GumpCommand.Deferred(this, x, y + Height - Constants.OBJECT_HANDLES_HP_BAR_HEIGHT, layerDepth + CHILD_LAYER_INCREMENT * 3, DRAW_HP_BAR)
                );
            }

            return true;
        }

        internal static Color HpBarColor(byte hitsPercentage) =>
            hitsPercentage >= 80 ? Color.Green
            : hitsPercentage >= 50 ? Color.YellowGreen
            : hitsPercentage >= 30 ? Color.Yellow
            : Color.Red;

        bool IGumpDeferredDraw.DrawDeferred(UltimaBatcher2D batcher, int x, int y, float layerDepth, int arg)
        {
            if (arg == DRAW_TEXT)
            {
                return _renderedText.Draw(batcher, Width, Height, x, y, Width, Height, 0, 0, layerDepth);
            }

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);
            int barHeight = Constants.OBJECT_HANDLES_HP_BAR_HEIGHT;

            batcher.Draw(SolidColorTextureCache.GetTexture(Color.Black), new Rectangle(x, y, _hpBarWidth, barHeight), hueVector, layerDepth);

            if (_hpBarFilled > 0)
            {
                batcher.Draw(SolidColorTextureCache.GetTexture(_hpBarColor), new Rectangle(x, y, _hpBarFilled, barHeight), hueVector, layerDepth + CHILD_LAYER_INCREMENT);
            }

            return true;
        }

        public override void Dispose()
        {
            _renderedText?.Destroy();
            base.Dispose();
        }
    }
}
