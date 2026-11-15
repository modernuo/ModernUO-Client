// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI.Gumps;

namespace ClassicUO.Game.Managers
{
    [Flags]
    internal enum NameOverheadTypeAllowed
    {
        None = 0,
        Items = 1 << 0,
        Corpses = 1 << 1,
        Innocent = 1 << 2,
        Ally = 1 << 3,
        Gray = 1 << 4,
        Criminal = 1 << 5,
        Enemy = 1 << 6,
        Murderer = 1 << 7,
        Invulnerable = 1 << 8,
        AllMobiles = Innocent | Ally | Gray | Criminal | Enemy | Murderer | Invulnerable,
        All = Items | Corpses | AllMobiles
    }

    internal sealed class NameOverHeadManager
    {
        private NameOverHeadHandlerGump _gump;
        private readonly World _world;

        public NameOverHeadManager(World world) { _world = world; }

        public NameOverheadTypeAllowed TypeAllowed
        {
            get => ProfileManager.CurrentProfile.NameOverheadTypeAllowed;
            set => ProfileManager.CurrentProfile.NameOverheadTypeAllowed = value;
        }

        public bool IsToggled
        {
            get => ProfileManager.CurrentProfile.NameOverheadToggled;
            set => ProfileManager.CurrentProfile.NameOverheadToggled = value;
        }

        /// <summary>The render-list walk calls this per object while name overheads are on, so the player's race (an ECS
        /// lookup) is read once per frame by the caller: <see cref="GameObject.PlayerIsGargoyle"/>.</summary>
        public bool IsAllowed(Entity entity, bool playerIsGargoyle) => IsAllowed(entity, TypeAllowed, playerIsGargoyle);

        internal static bool IsAllowed(Entity entity, NameOverheadTypeAllowed allowed, bool playerIsGargoyle)
        {
            if (entity == null)
            {
                return false;
            }

            if (SerialHelper.IsItem(entity.Serial))
            {
                if (entity is Item item)
                {
                    if (item.IsCorpse)
                        return allowed.HasFlag(NameOverheadTypeAllowed.Corpses);

                    if (!GameObject.CanBeDrawn(item.DisplayedGraphic, playerIsGargoyle))
                        return false;
                }

                return allowed.HasFlag(NameOverheadTypeAllowed.Items);
            }

            if (SerialHelper.IsMobile(entity.Serial) && entity is Mobile mobile)
            {
                return mobile.NotorietyFlag switch
                {
                    NotorietyFlag.Innocent => allowed.HasFlag(NameOverheadTypeAllowed.Innocent),
                    NotorietyFlag.Ally => allowed.HasFlag(NameOverheadTypeAllowed.Ally),
                    NotorietyFlag.Gray => allowed.HasFlag(NameOverheadTypeAllowed.Gray),
                    NotorietyFlag.Criminal => allowed.HasFlag(NameOverheadTypeAllowed.Criminal),
                    NotorietyFlag.Enemy => allowed.HasFlag(NameOverheadTypeAllowed.Enemy),
                    NotorietyFlag.Murderer => allowed.HasFlag(NameOverheadTypeAllowed.Murderer),
                    NotorietyFlag.Invulnerable => allowed.HasFlag(NameOverheadTypeAllowed.Invulnerable),
                    _ => true
                };
            }

            return false;
        }

        public void Open()
        {
            if (_gump == null || _gump.IsDisposed)
            {
                _gump = new NameOverHeadHandlerGump(_world);
                UIManager.Add(_gump);
            }

            _gump.IsEnabled = true;
            _gump.IsVisible = true;
        }

        public void SetMenuVisible(bool visible)
        {
            if (_gump != null && !_gump.IsDisposed)
            {
                _gump.IsVisible = visible;
            }
        }

        public void Close()
        {
            if (_gump == null)
            { //Required in case nameplates are active when closing and reopening the client
                _gump = new NameOverHeadHandlerGump(_world);
                UIManager.Add(_gump);
            }


            _gump.IsEnabled = false;
            _gump.IsVisible = false;
        }

        public void ToggleOverheads()
        {
            IsToggled = !IsToggled;
        }
    }
}
