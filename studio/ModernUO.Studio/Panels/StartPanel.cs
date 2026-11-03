using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;
using static SDL3.SDL;

namespace ModernUO.Studio.Panels
{
    /// <summary>Opening a pack: a path, the recent packs and the OS folder picker.</summary>
    internal sealed class StartPanel : Panel
    {
        private readonly IntPtr _window;
        private readonly SDL_DialogFileCallback _onFolder;
        private string _path = "";
        private volatile string _picked;

        public StartPanel(StudioContext context, IntPtr window) : base(context, true)
        {
            _window = window;
            _onFolder = OnFolderPicked;
        }

        public override string Title => "Start###start";
        public override string MenuName => "Start";
        public override string Icon => Icons.Sparkles;
        public override string MenuGroup => "Tools";
        public override bool NeedsPack => false;
        protected override Vector2 DefaultSize => new(640, 420);

        public void Browse() => SDL_ShowOpenFolderDialog(_onFolder, IntPtr.Zero, _window, string.IsNullOrEmpty(_path) ? null : _path, false);

        private void OnFolderPicked(IntPtr userData, IntPtr fileList, int filter)
        {
            // SDL may call back from another thread: hand the path to the next frame
            if (fileList == IntPtr.Zero)
            {
                return;
            }

            IntPtr first = Marshal.ReadIntPtr(fileList);

            if (first != IntPtr.Zero)
            {
                _picked = Marshal.PtrToStringUTF8(first);
            }
        }

        protected override void DrawContent()
        {
            if (_picked != null)
            {
                _path = _picked;
                _picked = null;
                Context.OpenPack(_path);
            }

            // a centred column, like a welcome page: never wider than reads comfortably
            float column = MathF.Min(640f, ImGui.GetContentRegionAvail().X);
            float indent = MathF.Max(0f, (ImGui.GetContentRegionAvail().X - column) * 0.5f);
            ImGui.Dummy(new Vector2(0f, 24f));
            ImGui.Indent(indent);

            ImGui.PushFont(Theme.Strong, 30f);
            ImGui.TextColored(Theme.Gold, "ModernUO");
            ImGui.SameLine(0f, 10f);
            ImGui.TextUnformatted("Studio");
            ImGui.PopFont();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + column);
            Ui.Muted("Browse and edit the assets and maps of a ModernUO pack. Studio reads packs made by Uo.Converter and never opens MUL or UOP files.");
            ImGui.PopTextWrapPos();
            ImGui.Dummy(new Vector2(0f, 12f));

            if (Context.Assets != null)
            {
                Ui.Chip($"{Icons.CircleCheck}  Open: {Context.Assets.Root}", Theme.Green);
                ImGui.Dummy(new Vector2(0f, 4f));
            }

            if (Ui.BeginCard("open", new Vector2(column, 0f), autoHeight: true))
            {
                Ui.SectionLabel("Open a pack");
                ImGui.SetNextItemWidth(column - 230f);
                bool enter = ImGui.InputTextWithHint("##path", "pack folder (the one with manifest.json)", ref _path, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();

                if (Ui.Button($"{Icons.FolderOpen}  Browse"))
                {
                    Browse();
                }

                ImGui.SameLine();

                if ((Ui.Button($"{Icons.ArrowRight}  Open", ButtonKind.Primary) || enter) && !string.IsNullOrWhiteSpace(_path))
                {
                    Context.OpenPack(_path.Trim().Trim('"'));
                }
            }

            Ui.EndCard();
            ImGui.Dummy(new Vector2(0f, 8f));
            Ui.SectionLabel("Recent");

            if (Context.Settings.RecentPacks.Count == 0)
            {
                Ui.Muted("Packs you open appear here.");
            }

            string open = null;
            var draw = ImGui.GetWindowDrawList();

            foreach (string recent in Context.Settings.RecentPacks)
            {
                bool exists = File.Exists(Path.Combine(recent, "manifest.json"));
                Vector2 min = ImGui.GetCursorScreenPos();
                var size = new Vector2(column, 50f);
                ImGui.PushID(recent);

                if (ImGui.InvisibleButton("recent", size) && exists)
                {
                    open = recent;
                }

                bool hovered = ImGui.IsItemHovered();
                float t = Ui.Animate(ImGui.GetItemID(), hovered && exists ? 1f : 0f);
                draw.AddRectFilled(min, min + size, Theme.U32(Theme.Mix(Theme.Surface, Theme.Raised, t)), Theme.Rounding);
                draw.AddText(min + new Vector2(14f, 15f), Theme.U32(exists ? Theme.Gold : Theme.TextMuted), Icons.Package);

                string name = Path.GetFileName(recent.TrimEnd('\\', '/'));
                ImGui.PushFont(Theme.Strong, 0f);
                draw.AddText(min + new Vector2(44f, 7f), Theme.U32(exists ? Theme.Text : Theme.TextMuted), name);
                ImGui.PopFont();
                draw.AddText(min + new Vector2(44f, 26f), Theme.U32(Theme.TextMuted), exists ? recent : $"{recent}  (missing)");
                ImGui.PopID();
                ImGui.Dummy(new Vector2(0f, 2f));
            }

            if (open != null)
            {
                _path = open;
                Context.OpenPack(open);
            }

            ImGui.Dummy(new Vector2(0f, 8f));
            Ui.SectionLabel("Make a pack");
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + column);
            Ui.Muted("Convert a UO install with the converter, then open the output folder here:");
            ImGui.PopTextWrapPos();
            const string command = "dotnet run -c Release --project poc/Uo.Converter -- --uo <uo-install> --out <pack>";

            if (Ui.BeginCard("cmd", new Vector2(column, 0f), sunken: true, autoHeight: true))
            {
                Ui.MonoText(command, Theme.TextSecondary);
                ImGui.SameLine(column - 48f);

                if (Ui.IconButton("copycmd", Icons.Copy, "Copy the command"))
                {
                    ImGui.SetClipboardText(command);
                }
            }

            Ui.EndCard();
            ImGui.Unindent(indent);        }
    }
}
