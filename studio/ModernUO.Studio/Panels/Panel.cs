using System.Numerics;
using Hexa.NET.ImGui;

namespace ModernUO.Studio.Panels
{
    /// <summary>A dockable Studio window. Panels that need a pack draw a hint instead while none is open.</summary>
    internal abstract class Panel
    {
        private bool _open;

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

        public virtual string MenuGroup => "Assets";

        public virtual bool NeedsPack => true;

        protected virtual Vector2 DefaultSize => new(900, 600);

        protected virtual ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.None;

        public bool IsOpen
        {
            get => _open;
            set => _open = value;
        }

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

            ImGui.SetNextWindowSize(DefaultSize, ImGuiCond.FirstUseEver);

            if (ImGui.Begin(Title, ref _open, WindowFlags))
            {
                if (NeedsPack && Context.Assets == null)
                {
                    ImGui.TextDisabled("No pack is open. File > Open Pack...");
                }
                else
                {
                    DrawContent();
                }
            }

            ImGui.End();
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
