// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// perf-8: a container window's item controls across content updates (0x25, 0x3C, removals, amount changes). The window
    /// still rebuilds its frame controls and walks the whole contents in order on every update, as before; what changes is
    /// that an item shown before keeps its control, reset to what a new control would hold (<see cref="ItemGump.Reset"/>),
    /// instead of a new allocation. Controls are added in the contents' order, so the child order (draw and hit order),
    /// positions, sizes, hues, visibility and tooltips are those of a full rebuild; items no longer shown lose their control.
    /// The full rebuild stays while a mouse button is held or a drag started on one of the window's item controls: there the
    /// rebuild's new control drops the press (no pickup, no click), and reusing it would change that.
    /// <c>CUO_CONTAINER_INCREMENTAL=0</c> (dev builds) forces the full rebuild.
    /// </summary>
    internal sealed class ContainerItemControls
    {
        /// <summary>Content updates done as full rebuilds / with controls reused, and controls reused / created (never reset).</summary>
        public static long FullRebuilds, IncrementalUpdates, ControlsReused, ControlsCreated;

#if CUO_DEVTOOLS
        private static readonly bool _envOff = Environment.GetEnvironmentVariable("CUO_CONTAINER_INCREMENTAL") == "0";
#else
        private const bool _envOff = false;
#endif

        internal static bool? OverrideForTests;

        /// <summary>Tests only: the contents filter without tile data (null: <c>ContentsArt.ShownInContainer</c>).</summary>
        internal static Func<Item, bool, bool> ShownForTests;

        public static bool Enabled => OverrideForTests ?? !_envOff;

        private readonly Dictionary<uint, ItemGump> _spare = new Dictionary<uint, ItemGump>();

        /// <summary>A mouse button is held or a drag started on one of <paramref name="gump"/>'s item controls.</summary>
        public static bool InputHeld(Control gump)
        {
            for (int b = (int)MouseButtonType.Left; b < (int)MouseButtonType.Size; b++)
            {
                Control c = UIManager.LastControlMouseDown((MouseButtonType)b);

                if (c is ItemGump && c.Parent == gump)
                {
                    return true;
                }
            }

            return UIManager.DraggingControl is ItemGump && UIManager.DraggingControl.Parent == gump;
        }

        /// <summary>
        /// Before the window rebuilds (in place of <c>Clear()</c>): with <paramref name="reuse"/>, takes its live item
        /// controls out of the child list and keeps them by serial; every other child is disposed and left in the list, as
        /// Clear left them, for Update to drop.
        /// </summary>
        public void Begin(Control gump, bool reuse)
        {
            _spare.Clear();
            List<Control> children = gump.Children;

            if (reuse)
            {
                int kept = 0;

                for (int i = 0; i < children.Count; i++)
                {
                    Control c = children[i];

                    if (c is ItemGump ig && !ig.IsDisposed && _spare.TryAdd(ig.LocalSerial, ig))
                    {
                        continue;
                    }

                    children[kept++] = c;
                }

                children.RemoveRange(kept, children.Count - kept);
                IncrementalUpdates++;
            }
            else
            {
                FullRebuilds++;
            }

            foreach (Control c in children)
            {
                c.Dispose();
            }
        }

        /// <summary>The control for an item about to be added: its previous one reset as the constructor sets it, or a new one.</summary>
        public ItemGump Get(Gump gump, uint serial, ushort graphic, ushort hue, int x, int y, bool isGump)
        {
            if (_spare.Remove(serial, out ItemGump ig))
            {
                ig.Reset(serial, graphic, hue, x, y);

                // no texture for the new graphic: the control disposed itself; a new one, also disposed, has a new one's size
                if (!ig.IsDisposed)
                {
                    ControlsReused++;

                    return ig;
                }
            }

            ControlsCreated++;

            return new ItemGump(gump, serial, graphic, hue, x, y, isGump);
        }

        /// <summary>After the rebuild: the controls of items no longer shown are disposed and left in the list, as Clear left them.</summary>
        public void End(Control gump)
        {
            foreach (ItemGump ig in _spare.Values)
            {
                ig.Dispose();
                gump.Children.Add(ig);
            }

            _spare.Clear();
        }

        /// <summary>
        /// ContainerGump's item controls for <paramref name="container"/>'s contents, in their order: the filter, graphic,
        /// visibility, scaled size and position of the controls it built before perf-8.
        /// </summary>
        public static void AddItems(Gump gump, ContainerItemControls controls, Entity container, bool isChessboard, bool isBackgammon,
            bool minimized, float scale)
        {
            bool isCorpse = container.Graphic == 0x2006;
            bool isGump = isChessboard || isBackgammon;

            // T2: the children from the ECS with ecs_read_contents on (WorldState.Children)
            foreach (Item item in gump.World.State.Children(container))
            {
                // NOTE: Switched from 'item.Layer' property which comes from server to 'ItemData.Layer' from tiledata.mul.
                //       In the past I found some issues using the server property.
                //       Probably lack of knowledge about some client behaviour.
                //       Remember it.

                // some items has layer = [face | beard | hair] and we need to check if it's a wearable item or not.
                // when the item is wearable we dont add it to the container.
                // Tested with --> client = 7.0.95.0 | graphic = 0x0A02
                if (!(ShownForTests?.Invoke(item, isCorpse) ?? global::ClassicUO.Game.Map.ContentsArt.ShownInContainer(item, isCorpse)))
                {
                    continue;
                }

                ItemGump itemControl = controls.Get(
                    gump,
                    item.Serial,
                    (ushort)(item.DisplayedGraphic - (isGump ? Constants.ITEM_GUMP_TEXTURE_OFFSET : 0)),
                    item.Hue,
                    item.X,
                    item.Y,
                    isGump
                );

                itemControl.IsVisible = !minimized;

                if (
                    ProfileManager.CurrentProfile != null
                    && ProfileManager.CurrentProfile.ScaleItemsInsideContainers
                )
                {
                    itemControl.Width = (int)(itemControl.Width * scale);
                    itemControl.Height = (int)(itemControl.Height * scale);
                }

                itemControl.X = (int)((short)item.X * scale);
                itemControl.Y = (int)(((short)item.Y - (isChessboard ? 20 : 0)) * scale);

                gump.Add(itemControl);
            }
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_CONTAINER_CONTROLS_CHECK=1 compares every update's item controls with the ones a full rebuild
        // builds (a fresh set on a scratch parent): order, serial, graphic, hue, position, size, flags, visibility, tooltip.
        public static readonly bool CheckOn = Environment.GetEnvironmentVariable("CUO_CONTAINER_CONTROLS_CHECK") == "1";
        public static long Checked, Mismatched;

        /// <summary>
        /// <paramref name="gump"/>'s children <paramref name="start"/>..<paramref name="end"/> (the controls this update's
        /// <see cref="AddItems"/> added) against a full rebuild's.
        /// </summary>
        public static void Verify(Gump gump, int start, int end, Entity container, bool isChessboard, bool isBackgammon, bool minimized,
            float scale)
        {
            if (container == null)
            {
                return;
            }

            long created = ControlsCreated;
            var shadow = new Gump(gump.World, 0, 0);
            var fresh = new ContainerItemControls();
            AddItems(shadow, fresh, container, isChessboard, isBackgammon, minimized, scale);
            ControlsCreated = created;

            string diff = Compare(gump.Children, start, end, shadow.Children);
            shadow.Dispose();
            Checked++;

            if (diff != null)
            {
                Mismatched++;

                if (Mismatched <= 20)
                {
                    ClassicUO.Utility.Logging.Log.Warn($"[container-check] 0x{gump.LocalSerial:X8}: {diff}");
                }
            }
        }

        /// <summary>Null when <paramref name="live"/>[start..end) holds what <paramref name="rebuilt"/> holds, else the first difference.</summary>
        internal static string Compare(List<Control> live, int start, int end, List<Control> rebuilt)
        {
            if (end - start != rebuilt.Count)
            {
                return $"{end - start} controls, a rebuild has {rebuilt.Count}";
            }

            for (int i = 0; i < rebuilt.Count; i++)
            {
                if (!(live[start + i] is ItemGump a) || !(rebuilt[i] is ItemGump b))
                {
                    return $"#{i} is not an item control";
                }

                string d = Describe(a);
                string e = Describe(b);

                if (d != e)
                {
                    return $"#{i} {d}, a rebuild has {e}";
                }
            }

            return null;
        }

        internal static string Describe(ItemGump c) =>
            $"0x{c.LocalSerial:X8} g=0x{c.Graphic:X4} h=0x{c.Hue:X4} {c.X},{c.Y} {c.Width}x{c.Height} partial={c.IsPartialHue} " +
            $"vis={c.IsVisible} disposed={c.IsDisposed} mouse={c.AcceptMouseInput} pick={c.CanPickUp} hl={c.HighlightOnMouseOver} " +
            $"move={c.CanMove} size={c.WantUpdateSize} page={c.Page} tip={c.Tooltip}";
#endif
    }
}
