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
        public override string Icon => Icons.ScrollText;
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

            if (Ui.Button($"{Icons.X}  Clear", ButtonKind.Ghost))
            {
                StudioLog.Clear();
            }

            ImGui.SameLine();
            Ui.Toggle("Follow", ref _autoScroll, "Keep the newest line in view");

            if (Ui.BeginCard("lines", Vector2.Zero, flags: ImGuiWindowFlags.HorizontalScrollbar, sunken: true))
            {
                ImGui.PushFont(Theme.Mono, Theme.SmallSize + 0.5f);

                foreach (var e in _entries)
                {
                    var (icon, color) = e.Level switch
                    {
                        StudioLogLevel.Error => (Icons.Alert, Theme.Red),
                        StudioLogLevel.Warning => (Icons.Warning, Theme.Amber),
                        _ => (Icons.ChevronRight, Theme.TextMuted)
                    };

                    ImGui.TextColored(Theme.TextMuted, e.Time.ToString("HH:mm:ss"));
                    ImGui.SameLine();
                    ImGui.TextColored(color, icon);
                    ImGui.SameLine();
                    ImGui.TextColored(e.Level == StudioLogLevel.Info ? Theme.TextSecondary : color, e.Text);
                }

                ImGui.PopFont();

                if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f)
                {
                    ImGui.SetScrollHereY(1f);
                }
            }

            Ui.EndCard();        }
    }
}
