// SPDX-License-Identifier: BSD-2-Clause

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
using System;
using System.Collections.Generic;
using System.Xml;

namespace ClassicUO.Game.UI.Gumps
{
    internal class PaperDollGump : TextContainerGump
    {
        private static readonly ushort[] PeaceModeBtnGumps = { 0x07e5, 0x07e6, 0x07e7 };
        private static readonly ushort[] WarModeBtnGumps = { 0x07e8, 0x07e9, 0x07ea };
        private GumpPic _combatBook,
            _racialAbilitiesBook;
        private HitBox _hitBox;
        private bool _isWarMode,
            _isMinimized;

        private PaperDollInteractable _paperDollInteractable;
        private GumpPic _partyManifestPic;

        private GumpPic _picBase;
        private GumpPic _profilePic;
        private readonly List<EquipmentSlot> _slots = new List<EquipmentSlot>();
        // perf-8: the mobile every slot's per-frame update looks at, one lookup while it stays valid
        private EntityRef _slotMobile;
        // perf-12: set by PrepareSlots for the children's Update of this gump update only (see there)
        private bool _slotsPrepared;
        private bool _hadMobile;
        private Mobile _preparedSlotMobile;
        private Label _titleLabel;
        private GumpPic _virtueMenuPic;
        private Button _warModeBtn;

        public PaperDollGump(World world) : base(world, 0, 0)
        {
            CanMove = true;
            CanCloseWithRightClick = true;
        }

        public PaperDollGump(World world, uint serial, bool canLift) : this(world)
        {
            LocalSerial = serial;
            CanLift = canLift;
            BuildGump();
        }

        public override GumpType GumpType => GumpType.PaperDoll;

        public bool IsMinimized
        {
            get => _isMinimized;
            set
            {
                if (_isMinimized != value)
                {
                    _isMinimized = value;

                    _picBase.Graphic = value
                        ? (ushort)0x7EE
                        : (ushort)(0x07d0 + (LocalSerial == World.Player ? 0 : 1));

                    foreach (Control c in Children)
                    {
                        c.IsVisible = !value;
                    }

                    _picBase.IsVisible = true;
                    WantUpdateSize = true;
                }
            }
        }

        public bool CanLift { get; set; }

        public override void Dispose()
        {
            UIManager.SavePosition(LocalSerial, Location);

            if (LocalSerial == World.Player)
            {
                if (_virtueMenuPic != null)
                {
                    _virtueMenuPic.MouseDoubleClick -= VirtueMenu_MouseDoubleClickEvent;
                }

                if (_partyManifestPic != null)
                {
                    _partyManifestPic.MouseDoubleClick -= PartyManifest_MouseDoubleClickEvent;
                }
            }

            Clear();
            base.Dispose();
        }

        private void _hitBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtonType.Left && !IsMinimized)
            {
                IsMinimized = true;
            }
        }

        private void BuildGump()
        {
            _picBase?.Dispose();
            _hitBox?.Dispose();

            var showPaperdollBooks =
                LocalSerial == World.Player && World.ClientFeatures.PaperdollBooks;
            var showRacialAbilitiesBook =
                showPaperdollBooks && Client.Game.UO.Version >= ClientVersion.CV_7000;

            if (LocalSerial == World.Player)
            {
                Add(_picBase = new GumpPic(0, 0, 0x07d0, 0));
                _picBase.MouseDoubleClick += _picBase_MouseDoubleClick;

                //HELP BUTTON
                Add(
                    new Button((int)Buttons.Help, 0x07ef, 0x07f0, 0x07f1)
                    {
                        X = 185,
                        Y = 44 + 27 * 0,
                        ButtonAction = ButtonAction.Activate
                    }
                );

                //OPTIONS BUTTON
                Add(
                    new Button((int)Buttons.Options, 0x07d6, 0x07d7, 0x07d8)
                    {
                        X = 185,
                        Y = 44 + 27 * 1,
                        ButtonAction = ButtonAction.Activate
                    }
                );

                // LOG OUT BUTTON
                Add(
                    new Button((int)Buttons.LogOut, 0x07d9, 0x07da, 0x07db)
                    {
                        X = 185,
                        Y = 44 + 27 * 2,
                        ButtonAction = ButtonAction.Activate
                    }
                );

                if (Client.Game.UO.Version < ClientVersion.CV_500A)
                {
                    // JOURNAL BUTTON
                    Add(
                        new Button((int)Buttons.Journal, 0x7dc, 0x7dd, 0x7de)
                        {
                            X = 185,
                            Y = 44 + 27 * 3,
                            ButtonAction = ButtonAction.Activate
                        }
                    );
                }
                else
                {
                    // QUESTS BUTTON
                    Add(
                        new Button((int)Buttons.Quests, 0x57b5, 0x57b7, 0x57b6)
                        {
                            X = 185,
                            Y = 44 + 27 * 3,
                            ButtonAction = ButtonAction.Activate
                        }
                    );
                }

                // SKILLS BUTTON
                Add(
                    new Button((int)Buttons.Skills, 0x07df, 0x07e0, 0x07e1)
                    {
                        X = 185,
                        Y = 44 + 27 * 4,
                        ButtonAction = ButtonAction.Activate
                    }
                );

                // GUILD BUTTON
                Add(
                    new Button((int)Buttons.Guild, 0x57b2, 0x57b4, 0x57b3)
                    {
                        X = 185,
                        Y = 44 + 27 * 5,
                        ButtonAction = ButtonAction.Activate
                    }
                );

                // TOGGLE PEACE/WAR BUTTON
                Mobile mobile = World.FindMobile(LocalSerial);

                _isWarMode = mobile?.InWarMode ?? false;
                ushort[] btngumps = _isWarMode ? WarModeBtnGumps : PeaceModeBtnGumps;

                Add(
                    _warModeBtn = new Button(
                        (int)Buttons.PeaceWarToggle,
                        btngumps[0],
                        btngumps[1],
                        btngumps[2]
                    )
                    {
                        X = 185,
                        Y = 44 + 27 * 6,
                        ButtonAction = ButtonAction.Activate
                    }
                );

                int profileX = 25;
                const int SCROLLS_STEP = 14;

                if (showRacialAbilitiesBook)
                {
                    profileX += SCROLLS_STEP;
                }

                Add(_profilePic = new GumpPic(profileX, 196, 0x07D2, 0));
                _profilePic.MouseDoubleClick += Profile_MouseDoubleClickEvent;

                profileX += SCROLLS_STEP;

                Add(_partyManifestPic = new GumpPic(profileX, 196, 0x07D2, 0));
                _partyManifestPic.MouseDoubleClick += PartyManifest_MouseDoubleClickEvent;

                _hitBox = new HitBox(228, 260, 16, 16);
                _hitBox.MouseUp += _hitBox_MouseUp;

                Add(_hitBox);
            }
            else
            {
                Add(_picBase = new GumpPic(0, 0, 0x07d1, 0));
                Add(_profilePic = new GumpPic(25, 196, 0x07D2, 0));
                _profilePic.MouseDoubleClick += Profile_MouseDoubleClickEvent;
            }

            // STATUS BUTTON
            Add(
                new Button((int)Buttons.Status, 0x07eb, 0x07ec, 0x07ed)
                {
                    X = 185,
                    Y = 44 + 27 * 7,
                    ButtonAction = ButtonAction.Activate
                }
            );

            // Virtue menu
            Add(_virtueMenuPic = new GumpPic(80, 4, 0x0071, 0));
            _virtueMenuPic.MouseDoubleClick += VirtueMenu_MouseDoubleClickEvent;

            // Equipment slots for hat/earrings/neck/ring/bracelet

            // add left
            var initialPos = new Point(2, 70);

            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y, Layer.Helmet, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21, Layer.Earrings, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 2, Layer.Necklace, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 3, Layer.Ring, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 4, Layer.Bracelet, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 5, Layer.Tunic, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 6, Layer.OneHanded, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 7, Layer.TwoHanded, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 8, Layer.Talisman, this));

            // add right
            initialPos.X = 160 + 2;
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y, Layer.Robe, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21, Layer.Gloves, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 2, Layer.Pants, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 3, Layer.Arms, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 4, Layer.Cloak, this));
            _slots.Add(new EquipmentSlot(0, initialPos.X, initialPos.Y + 21 * 5, Layer.Shoes, this));

            foreach (var slot in _slots)
                Add(slot);

            // Paperdoll control!
            _paperDollInteractable = new PaperDollInteractable(8, 19, LocalSerial, this);
            Add(_paperDollInteractable);

            if (showPaperdollBooks)
            {
                Add(_combatBook = new GumpPic(156, 200, 0x2B34, 0));
                _combatBook.MouseDoubleClick += (sender, e) =>
                {
                    GameActions.OpenAbilitiesBook(World);
                };

                if (showRacialAbilitiesBook)
                {
                    Add(_racialAbilitiesBook = new GumpPic(23, 200, 0x2B28, 0));

                    _racialAbilitiesBook.MouseDoubleClick += (sender, e) =>
                    {
                        if (UIManager.GetGump<RacialAbilitiesBookGump>() == null)
                        {
                            UIManager.Add(new RacialAbilitiesBookGump(World, 100, 100));
                        }
                    };
                }
            }

            // Name and title
            _titleLabel = new Label("", false, 0x0386, 185, font: 1) { X = 39, Y = 262 };

            Add(_titleLabel);

            RequestUpdateContents();
        }

        private void _picBase_MouseDoubleClick(object sender, MouseDoubleClickEventArgs e)
        {
            if (e.Button == MouseButtonType.Left && IsMinimized)
            {
                IsMinimized = false;
            }
        }

        public void UpdateTitle(string text)
        {
            _titleLabel.Text = text;
        }

        private void VirtueMenu_MouseDoubleClickEvent(object sender, MouseDoubleClickEventArgs args)
        {
            if (args.Button == MouseButtonType.Left)
            {
                GameActions.ReplyGump(
                    World.Player,
                    0x000001CD,
                    0x00000001,
                    new[] { LocalSerial },
                    new Tuple<ushort, string>[0]
                );
            }
        }

        private void Profile_MouseDoubleClickEvent(object o, MouseDoubleClickEventArgs args)
        {
            if (args.Button == MouseButtonType.Left)
            {
                GameActions.RequestProfile(LocalSerial);
            }
        }

        private void PartyManifest_MouseDoubleClickEvent(
            object sender,
            MouseDoubleClickEventArgs args
        )
        {
            if (args.Button == MouseButtonType.Left)
            {
                PartyGump party = UIManager.GetGump<PartyGump>();

                if (party == null)
                {
                    int x = Client.Game.ClientBounds.Width / 2 - 272;
                    int y = Client.Game.ClientBounds.Height / 2 - 240;
                    UIManager.Add(new PartyGump(World, x, y, World.Party.CanLoot));
                }
                else
                {
                    party.BringOnTop();
                }
            }
        }

        public override void Update()
        {
            if (IsDisposed)
            {
                return;
            }

            Mobile mobile = World.FindMobile(LocalSerial);

            // the paperdoll closes when its mobile is destroyed (Mobile.Destroy closes it for every mobile but the player).
            // T9 slice 3: a destroyed mobile not yet swept is not found, so "the mobile shown before is gone" stands for it
            if (mobile != null ? mobile.IsDestroyed : _hadMobile)
            {
                Dispose();

                return;
            }

            _hadMobile = mobile != null;

            // This is to update the state of the war mode button.
            if (mobile != null && _isWarMode != mobile.InWarMode && LocalSerial == World.Player)
            {
                _isWarMode = mobile.InWarMode;
                ushort[] btngumps = _isWarMode ? WarModeBtnGumps : PeaceModeBtnGumps;
                _warModeBtn.ButtonGraphicNormal = btngumps[0];
                _warModeBtn.ButtonGraphicPressed = btngumps[1];
                _warModeBtn.ButtonGraphicOver = btngumps[2];
            }

            PrepareSlots();

            try
            {
                base.Update();
            }
            finally
            {
                _slotsPrepared = false;
                _preparedSlotMobile = null;
            }

            if (_paperDollInteractable != null && (CanLift || LocalSerial == World.Player.Serial))
            {
                bool force_false =
                    SelectedObject.Object is Item item
                    && (item.Layer == Layer.Backpack || item.ItemData.IsContainer);

                if (
                    _paperDollInteractable.HasFakeItem && !Client.Game.UO.GameCursor.ItemHold.Enabled
                    || force_false
                )
                {
                    _paperDollInteractable.SetFakeItem(false);
                }
                else if (
                    !_paperDollInteractable.HasFakeItem
                    && Client.Game.UO.GameCursor.ItemHold.Enabled
                    && !Client.Game.UO.GameCursor.ItemHold.IsFixedPosition
                    && UIManager.MouseOverControl?.RootParent == this
                )
                {
                    if (Client.Game.UO.GameCursor.ItemHold.ItemData.AnimID != 0)
                    {
                        if (
                            mobile != null
                            && mobile.FindItemByLayer(
                                (Layer)Client.Game.UO.GameCursor.ItemHold.ItemData.Layer
                            ) == null
                        )
                        {
                            _paperDollInteractable.SetFakeItem(true);
                        }
                    }
                }
            }
        }

        protected override void OnMouseExit(int x, int y)
        {
            _paperDollInteractable?.SetFakeItem(false);
        }

        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left && World.InGame)
            {
                Mobile container = World.FindMobile(LocalSerial);

                if (Client.Game.UO.GameCursor.ItemHold.Enabled)
                {
                    if (CanLift || LocalSerial == World.Player.Serial)
                    {
                        if (
                            SelectedObject.Object is Item item
                            && (item.Layer == Layer.Backpack || item.ItemData.IsContainer)
                        )
                        {
                            GameActions.DropItem(
                                Client.Game.UO.GameCursor.ItemHold.Serial,
                                0xFFFF,
                                0xFFFF,
                                0,
                                item.Serial
                            );

                            Mouse.CancelDoubleClick = true;
                        }
                        else
                        {
                            if (Client.Game.UO.GameCursor.ItemHold.ItemData.IsWearable)
                            {
                                Item equipment = container.FindItemByLayer(
                                    (Layer)Client.Game.UO.GameCursor.ItemHold.ItemData.Layer
                                );

                                if (equipment == null)
                                {
                                    GameActions.Equip(World,
                                        LocalSerial != World.Player ? container : World.Player
                                    );
                                    Mouse.CancelDoubleClick = true;
                                }
                            }
                        }
                    }
                }
                else if (SelectedObject.Object is Item item)
                {
                    if (World.TargetManager.IsTargeting)
                    {
                        World.TargetManager.Target(item.Serial);
                        Mouse.CancelDoubleClick = true;
                        Mouse.LastLeftButtonClickTime = 0;

                        if (World.TargetManager.TargetingState == CursorTarget.SetTargetClientSide)
                        {
                            UIManager.Add(new InspectorGump(World, item));
                        }
                    }
                    else if (!World.DelayedObjectClickManager.IsEnabled)
                    {
                        Point off = Mouse.LDragOffset;

                        World.DelayedObjectClickManager.Set(
                            item.Serial,
                            Mouse.Position.X - off.X - ScreenCoordinateX,
                            Mouse.Position.Y - off.Y - ScreenCoordinateY,
                            Time.Ticks + Mouse.MOUSE_DELAY_DOUBLE_CLICK
                        );
                    }
                }
            }
            else
            {
                base.OnMouseUp(x, y, button);
            }
        }

        public override void Save(XmlTextWriter writer)
        {
            base.Save(writer);

            writer.WriteAttributeString("isminimized", IsMinimized.ToString());
        }

        public override void Restore(XmlElement xml)
        {
            base.Restore(xml);

            if (LocalSerial == World.Player)
            {
                BuildGump();

                //GameActions.DoubleClick(0x8000_0000 | LocalSerial);
                Client.Game.GetScene<GameScene>()?.DoubleClickDelayed(LocalSerial);

                IsMinimized = bool.Parse(xml.GetAttribute("isminimized"));
            }
            else
            {
                Dispose();
            }
        }

        protected override void UpdateContents()
        {
            Mobile mobile = World.FindMobile(LocalSerial);

            if (mobile != null && mobile.Title != _titleLabel.Text)
            {
                UpdateTitle(mobile.Title);
            }

            _paperDollInteractable.RequestUpdate();

            if (mobile != null)
            {
                // perf-12: one worn view for both loops (FindItemByLayer validated the WornCache per slot per loop)
                WornItems worn = World.State.WornView(mobile);

                for (int i = 0; i < _slots.Count; i++)
                {
                    _slots[i].LocalSerial = worn[_slots[i].Layer]?.Serial ?? 0;
                }

                // art_container_prefetch: the slots' item art decoded on the workers before their controls look it up
                if (_slots.Count > 1 && global::ClassicUO.Game.Map.ContentsArt.Enabled)
                {
                    Span<uint> graphics = stackalloc uint[32];
                    int n = 0;

                    for (int i = 0; i < _slots.Count && n < graphics.Length; i++)
                    {
                        Item item = worn[_slots[i].Layer];

                        if (item != null)
                        {
                            graphics[n++] = item.DisplayedGraphic;
                        }
                    }

                    global::ClassicUO.Game.Map.ContentsArt.Prefetch(graphics.Slice(0, n));
                }
            }
        }

        public override void OnButtonClick(int buttonID)
        {
            if (
                Client.Game.UO.GameCursor.ItemHold.Enabled
                && !Client.Game.UO.GameCursor.ItemHold.IsFixedPosition
            )
            {
                OnMouseUp(0, 0, MouseButtonType.Left);

                return;
            }

            switch ((Buttons)buttonID)
            {
                case Buttons.Help:
                    GameActions.RequestHelp();

                    break;

                case Buttons.Options:
                    GameActions.OpenSettings(World);

                    break;

                case Buttons.LogOut:
                    Client.Game.GetScene<GameScene>()?.RequestQuitGame();

                    break;

                case Buttons.Journal:
                    GameActions.OpenJournal(World);

                    break;

                case Buttons.Quests:
                    GameActions.RequestQuestMenu(World);

                    break;

                case Buttons.Skills:
                    GameActions.OpenSkills(World);

                    break;

                case Buttons.Guild:
                    GameActions.OpenGuildGump(World);

                    break;

                case Buttons.PeaceWarToggle:
                    GameActions.ToggleWarMode(World.Player);

                    break;

                case Buttons.Status:

                    if (LocalSerial == World.Player)
                    {
                        UIManager.GetGump<BaseHealthBarGump>(LocalSerial)?.Dispose();

                        StatusGumpBase status = StatusGumpBase.GetStatusGump();

                        if (status == null)
                        {
                            UIManager.Add(
                                StatusGumpBase.AddStatusGump(World,
                                    Mouse.Position.X - 100,
                                    Mouse.Position.Y - 25
                                )
                            );
                        }
                        else
                        {
                            status.BringOnTop();
                        }
                    }
                    else
                    {
                        if (UIManager.GetGump<BaseHealthBarGump>(LocalSerial) != null)
                        {
                            break;
                        }

                        if (ProfileManager.CurrentProfile.CustomBarsToggled)
                        {
                            Rectangle bounds = new Rectangle(
                                0,
                                0,
                                HealthBarGumpCustom.HPB_WIDTH,
                                HealthBarGumpCustom.HPB_HEIGHT_SINGLELINE
                            );

                            UIManager.Add(
                                new HealthBarGumpCustom(World, LocalSerial)
                                {
                                    X = Mouse.Position.X - (bounds.Width >> 1),
                                    Y = Mouse.Position.Y - 5
                                }
                            );
                        }
                        else
                        {
                            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(0x0804);

                            UIManager.Add(
                                new HealthBarGump(World, LocalSerial)
                                {
                                    X = Mouse.Position.X - (gumpInfo.UV.Width >> 1),
                                    Y = Mouse.Position.Y - 5
                                }
                            );
                        }
                    }

                    break;
            }
        }

        private enum Buttons
        {
            Help,
            Options,
            LogOut,
            Journal,
            Quests,
            Skills,
            Guild,
            PeaceWarToggle,
            Status
        }

        private Mobile SlotMobile() => _slotMobile.Mobile(World, LocalSerial);

        /// <summary>
        /// perf-12: the slot mobile and every slot's worn item, read before the children update through one worn view
        /// (WorldState.WornView: the mobile's WornCache validated once, not once per slot by FindItemByLayer). Valid only
        /// during this gump's base.Update: gumps and controls do not write world state in Update (the funnel's survey: the
        /// only gump-side write is ContainerGump's clamp, from 0x25's handler), so the answers are what each slot's own
        /// lookup would read.
        /// </summary>
        private void PrepareSlots()
        {
            Mobile mobile = SlotMobile();
            _preparedSlotMobile = mobile;
            _slotsPrepared = true;

            if (mobile != null)
            {
                WornItems worn = World.State.WornView(mobile);

                for (int i = 0; i < _slots.Count; i++)
                {
                    _slots[i].SetWorn(worn[_slots[i].Layer]);
                }
            }
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_PAPERDOLL_SLOTS_CHECK=1 compares every slot's prepared worn item with its own FindItemByLayer
        public static readonly bool SlotsCheckOn = Environment.GetEnvironmentVariable("CUO_PAPERDOLL_SLOTS_CHECK") == "1";
        public static long SlotsChecked, SlotsMismatched;

        private static void CheckSlot(Mobile mobile, Layer layer, Item prepared)
        {
            Item own = mobile.FindItemByLayer(layer);
            SlotsChecked++;

            if (!ReferenceEquals(own, prepared))
            {
                SlotsMismatched++;

                if (SlotsMismatched <= 20)
                {
                    ClassicUO.Utility.Logging.Log.Warn($"[paperdoll-slots-check] 0x{mobile.Serial:X8} {layer}: prepared 0x{prepared?.Serial ?? 0:X8}, lookup 0x{own?.Serial ?? 0:X8}");
                }
            }
        }
#endif

        /// <summary>The slots' mobile: the one <see cref="PrepareSlots"/> read during this gump's update, else a lookup.</summary>
        private Mobile UpdatingSlotMobile(out bool prepared)
        {
            prepared = _slotsPrepared;

            return prepared ? _preparedSlotMobile : SlotMobile();
        }

        private class EquipmentSlot : Control
        {
            private ItemGumpFixed _itemGump;
            private readonly PaperDollGump _paperDollGump;

            public EquipmentSlot(
                uint serial,
                int x,
                int y,
                Layer layer,
                PaperDollGump paperDollGump
            )
            {
                X = x;
                Y = y;
                LocalSerial = serial;
                Width = 19;
                Height = 20;
                _paperDollGump = paperDollGump;
                Layer = layer;

                Add(new GumpPicTiled(0, 0, 19, 20, 0x243A) { AcceptMouseInput = false });

                Add(new GumpPic(0, 0, 0x2344, 0) { AcceptMouseInput = false });

                AcceptMouseInput = true;

                WantUpdateSize = false;
            }

            public Layer Layer { get; }

            private string _emptySlotTooltip;
            private EntityRef _item;
            // perf-12: this update's worn item on Layer, from the gump's PrepareSlots
            private bool _wornSet;
            private Item _worn;

            internal void SetWorn(Item item)
            {
                _worn = item;
                _wornSet = true;
            }

            public override void Update()
            {
                Item item = _item.Item(_paperDollGump.World, LocalSerial);

                if (item == null || item.IsDestroyed)
                {
                    _itemGump?.Dispose();
                    _itemGump = null;

                    // built once and set only when it isn't already the tooltip (this runs every frame)
                    _emptySlotTooltip ??= $"{Layer} slot";

                    if (!ReferenceEquals(Tooltip, _emptySlotTooltip))
                    {
                        SetTooltip(_emptySlotTooltip);
                    }
                }

                Mobile mobile = _paperDollGump.UpdatingSlotMobile(out bool prepared);
                bool fromView = prepared && _wornSet;
                Item worn = _worn;
                _wornSet = false;
                _worn = null;

                if (mobile != null)
                {
                    Item it_at_layer = fromView ? worn : mobile.FindItemByLayer(Layer);
#if CUO_DEVTOOLS
                    if (fromView && SlotsCheckOn)
                    {
                        CheckSlot(mobile, Layer, it_at_layer);
                    }
#endif

                    if ((it_at_layer != null && _itemGump != null && _itemGump.LocalSerial != it_at_layer.Serial) || _itemGump == null)
                    {
                        if (_itemGump != null)
                        {
                            _itemGump.Dispose();
                            _itemGump = null;
                        }

                        item = it_at_layer;

                        if (item != null)
                        {
                            LocalSerial = it_at_layer.Serial;

                            Add(
                                _itemGump = new ItemGumpFixed(_paperDollGump, item, 18, 18)
                                {
                                    X = 0,
                                    Y = 0,
                                    Width = 18,
                                    Height = 18,
                                    HighlightOnMouseOver = false,
                                    CanPickUp =
                                        _paperDollGump.World.InGame
                                        && (
                                            _paperDollGump.World.Player.Serial == _paperDollGump.LocalSerial
                                            || _paperDollGump.CanLift
                                        )
                                }
                            );
                        }
                    }
                }

                base.Update();
            }

            private class ItemGumpFixed : ItemGump
            {
                private readonly PaperDollGump _gump;
                private Point _originalSize;
                private Point _point;
                private Rectangle _rect;
                private bool _boundsSet;

                public ItemGumpFixed(PaperDollGump gump, Item item, int w, int h)
                    : base(gump, item.Serial, item.DisplayedGraphic, item.Hue, item.X, item.Y)
                {
                    _gump = gump;
                    Width = w;
                    Height = h;
                    WantUpdateSize = false;
                }

                // perf-6: the art's visible bounds exist once it is decoded, so they are read at the first draw (the
                // constructor's graphic, as before) instead of decoding the art at construction
                private void SetBounds()
                {
                    _boundsSet = true;
                    _rect = Client.Game.UO.Arts.GetRealArtBounds(Graphic);

                    _originalSize.X = Width;
                    _originalSize.Y = Height;

                    if (_rect.Width < Width)
                    {
                        _originalSize.X = _rect.Width;
                        _point.X = (Width >> 1) - (_originalSize.X >> 1);
                    }

                    if (_rect.Height < Height)
                    {
                        _originalSize.Y = _rect.Height;
                        _point.Y = (Height >> 1) - (_originalSize.Y >> 1);
                    }
                }

                public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
                {
                    Item item = ShownItem();

                    if (item == null)
                    {
                        Dispose();
                    }

                    if (IsDisposed)
                    {
                        return false;
                    }

                    Vector3 hueVector = ShaderHueTranslator.GetHueVector(
                        MouseIsOver && HighlightOnMouseOver ? 0x0035 : item.Hue,
                        item.ItemData.IsPartialHue,
                        1,
                        true
                    );

                    ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(item.DisplayedGraphic);

                    if (!_boundsSet)
                    {
                        Client.Game.UO.Arts.GetArt(Graphic);
                        SetBounds();
                    }

                    var texture = artInfo.Texture;
                    if (texture != null)
                    {
                        float layerDepth = layerDepthRef;
                        var sourceRectangle = artInfo.UV;
                        // a value command: the closure it replaces (plus its display class) was allocated per slot per frame
                        renderLists.AddGumpWithAtlas
                        (
                            GumpCommand.Sprite
                            (
                                texture,
                                new Rectangle(x + _point.X, y + _point.Y, _originalSize.X, _originalSize.Y),
                                new Rectangle(sourceRectangle.X + _rect.X, sourceRectangle.Y + _rect.Y, _rect.Width, _rect.Height),
                                hueVector,
                                layerDepth
                            )
                        );

                        return true;
                    }

                    return false;
                }

                public override bool Contains(int x, int y)
                {
                    return true;
                }
            }
        }
    }
}
