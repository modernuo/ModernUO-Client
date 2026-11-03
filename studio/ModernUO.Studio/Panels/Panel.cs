using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>A dockable Studio window. Panels that need a pack draw a hint instead while none is open.</summary>
    internal abstract class Panel
    {
        private bool _open;
        private ImGuiWindowClassPtr _class;

        protected Panel(StudioContext context, bool open)
        {
            Context = context;
            _open = open;
        }

        protected StudioContext Context { get; }

        /// <summary>The window name: a stable "###id" suffix keeps the docking layout when the visible title changes.</summary>
        public abstract string Title { get; }

        /// <summary>The View menu entry.</summary>
        public abstract string MenuName { get; }

        /// <summary>The Lucide icon in the panel's tab and menu entry (Gui.Icons).</summary>
        public virtual string Icon => null;

        public virtual string MenuGroup => "Assets";

        public virtual bool NeedsPack => true;

        protected virtual Vector2 DefaultSize => new(900, 600);

        protected virtual ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.None;

        public bool IsOpen
        {
            get => _open;
            set => _open = value;
        }

        // the visible tab text carries the icon; "###id" keeps the window's identity (layouts, focus) independent of it
        private string WindowName => Icon == null ? Title : $"{Icon}  {Title}";

        public void Focus()
        {
            _open = true;
            ImGui.SetWindowFocus(Title);
        }

        public void Draw()
        {
            if (!_open)
            {
                return;
            }

            // no dock-node close button or window menu: a panel closes from its own tab, and the View menu reopens it
            if (_class.IsNull)
            {
                _class = ImGui.ImGuiWindowClass();
                _class.DockNodeFlagsOverrideSet = (ImGuiDockNodeFlags)(ImGuiDockNodeFlagsPrivate.NoWindowMenuButton | ImGuiDockNodeFlagsPrivate.NoCloseButton);
            }

            ImGui.SetNextWindowClass(_class);
            ImGui.SetNextWindowSize(DefaultSize, ImGuiCond.FirstUseEver);

            if (ImGui.Begin(WindowName, ref _open, WindowFlags | ImGuiWindowFlags.NoCollapse))
            {
                if (NeedsPack && Context.Assets == null)
                {
                    EmptyState(Icons.Package, "No pack open", "Open a pack from File > Open Pack, or from the Start panel.");
                }
                else
                {
                    DrawContent();
                }
            }

            ImGui.End();
        }

        /// <summary>A centred icon, title and hint for a panel with nothing to show.</summary>
        protected static void EmptyState(string icon, string title, string hint)
        {
            Vector2 avail = ImGui.GetContentRegionAvail();
            Vector2 start = ImGui.GetCursorScreenPos();
            float y = start.Y + MathF.Max(0f, avail.Y * 0.35f - 40f);
            var draw = ImGui.GetWindowDrawList();

            ImGui.PushFont(Theme.Body, 34f);
            Vector2 iconSize = ImGui.CalcTextSize(icon);
            draw.AddText(new Vector2(start.X + (avail.X - iconSize.X) * 0.5f, y), Theme.U32(Theme.TextMuted), icon);
            ImGui.PopFont();

            ImGui.PushFont(Theme.Strong, 0f);
            Vector2 titleSize = ImGui.CalcTextSize(title);
            draw.AddText(new Vector2(start.X + (avail.X - titleSize.X) * 0.5f, y + iconSize.Y + 12f), Theme.U32(Theme.Text), title);
            ImGui.PopFont();

            Vector2 hintSize = ImGui.CalcTextSize(hint);
            draw.AddText(new Vector2(start.X + (avail.X - hintSize.X) * 0.5f, y + iconSize.Y + titleSize.Y + 18f), Theme.U32(Theme.TextSecondary), hint);
        }

        protected abstract void DrawContent();

        /// <summary>-select: select an entry by id (art, gump, hue, body, multi), for smoke tests and links.</summary>
        public virtual void SelectById(int id)
        {
        }

        /// <summary>The open pack changed (opened, closed or replaced): drop everything derived from the old one.</summary>
        public virtual void OnPackChanged()
        {
        }
    }
}
