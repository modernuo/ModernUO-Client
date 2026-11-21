using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using Microsoft.Xna.Framework;

namespace ClassicUO.UnitTests.Game.UI
{
    // UIManager and Mouse are process-wide statics: a test that drives them takes this scope (inside the
    // FunnelCheckCollection, which runs alone), starts from an empty gump list and no input state, and puts everything back.
    internal sealed class UiTestScope : IDisposable
    {
        private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;

        private readonly List<Gump> _saved;
        private readonly Point _mouse;
        private readonly Point? _override;

        public UiTestScope()
        {
            _saved = UIManager.Gumps.ToList();
            _mouse = Mouse.Position;
            _override = Mouse.Override;
            UIManager.Gumps.Clear();
            ResetInput();
        }

        public void Dispose()
        {
            ResetInput();
            UIManager.Gumps.Clear();

            foreach (Gump g in _saved)
            {
                UIManager.Gumps.AddLast(g);
            }

            Mouse.Position = _mouse;
            Mouse.Override = _override;
        }

        public static void ResetInput()
        {
            Array.Clear(MouseDownControls);
            Set("_keyboardFocusControl", null);
            Set("_lastFocus", null);
            Set("_isDraggingControl", false);
            Set("_needSort", false);
            typeof(UIManager).GetProperty(nameof(UIManager.MouseOverControl))!.SetValue(null, null);
            typeof(UIManager).GetProperty(nameof(UIManager.DraggingControl))!.SetValue(null, null);
            Mouse.Override = null;
        }

        public static Control[] MouseDownControls => (Control[])typeof(UIManager).GetField("_mouseDownControls", Static)!.GetValue(null);

        public static Control LastFocus => (Control)typeof(UIManager).GetField("_lastFocus", Static)!.GetValue(null);

        public static Control Hover(int x, int y)
        {
            Mouse.Position = new Point(x, y);

            return (Control)typeof(UIManager).GetMethod("GetMouseOverControl", Static)!.Invoke(null, new object[] { Mouse.Position });
        }

        // UIManager.HandleMouseInput: the per-frame hover update (Enter/Exit/Over, MouseOverControl)
        public static void HandleMouse(int x, int y)
        {
            Mouse.Position = new Point(x, y);
            typeof(UIManager).GetMethod("HandleMouseInput", Static)!.Invoke(null, null);
        }

        public static void StartDrag(Control root, Point origin)
        {
            typeof(UIManager).GetProperty(nameof(UIManager.DraggingControl))!.SetValue(null, root);
            Set("_isDraggingControl", true);
            Set("_dragOrigin", origin);
        }

        public static void EndDrag(Point at)
        {
            Mouse.Position = at;
            typeof(UIManager).GetMethod("EndDragControl", Static)!.Invoke(null, new object[] { at });
        }

        private static void Set(string field, object value) => typeof(UIManager).GetField(field, Static)!.SetValue(null, value);
    }
}
