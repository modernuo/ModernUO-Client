using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    internal sealed class LogPanel : Panel
    {
        private readonly List<StudioLog.Entry> _entries = new();
        private int _version = -1;
        private bool _autoScroll = true;

        public LogPanel(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Log###log";
        public override string MenuName => "Log";
        public override string MenuGroup => "Tools";
        public override bool NeedsPack => false;
        protected override Vector2 DefaultSize => new(800, 200);

        protected override void DrawContent()
        {
            if (StudioLog.Version != _version)
            {
                _version = StudioLog.Version;
                StudioLog.CopyTo(_entries);
            }

            if (ImGui.SmallButton("Clear"))
            {
                StudioLog.Clear();
            }

            ImGui.SameLine();
            ImGui.Checkbox("auto-scroll", ref _autoScroll);

            if (ImGui.BeginChild("lines", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
            {
                foreach (var e in _entries)
                {
                    var color = e.Level switch
                    {
                        StudioLogLevel.Error => Widgets.Bad,
                        StudioLogLevel.Warning => Widgets.Warn,
                        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f)
                    };

                    ImGui.TextColored(Widgets.Dim, e.Time.ToString("HH:mm:ss"));
                    ImGui.SameLine();
                    ImGui.TextColored(color, e.Text);
                }

                if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f)
                {
                    ImGui.SetScrollHereY(1f);
                }
            }

            ImGui.EndChild();
        }
    }
}
