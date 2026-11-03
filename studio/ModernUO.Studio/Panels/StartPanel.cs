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

            ImGui.TextUnformatted("ModernUO Studio");
            ImGui.TextColored(Widgets.Dim, "Asset browsers for ModernUO packs. Studio reads packs made by Uo.Converter; it never opens MUL or UOP files.");
            ImGui.Spacing();

            if (Context.Assets != null)
            {
                ImGui.TextColored(Widgets.Good, $"Open: {Context.Assets.Root}");
                ImGui.Spacing();
            }

            ImGui.SetNextItemWidth(-160f);
            bool enter = ImGui.InputTextWithHint("##path", "pack folder (holds manifest.json)", ref _path, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();

            if (ImGui.Button("Browse..."))
            {
                Browse();
            }

            ImGui.SameLine();

            if ((ImGui.Button("Open") || enter) && !string.IsNullOrWhiteSpace(_path))
            {
                Context.OpenPack(_path.Trim().Trim('"'));
            }

            ImGui.Spacing();
            ImGui.SeparatorText("Recent packs");

            if (Context.Settings.RecentPacks.Count == 0)
            {
                ImGui.TextDisabled("None yet.");
            }

            string open = null;

            foreach (string recent in Context.Settings.RecentPacks)
            {
                bool exists = File.Exists(Path.Combine(recent, "manifest.json"));

                if (!exists)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Selectable(recent))
                {
                    open = recent;
                }

                if (!exists)
                {
                    ImGui.EndDisabled();
                }
            }

            if (open != null)
            {
                _path = open;
                Context.OpenPack(open);
            }

            ImGui.Spacing();
            ImGui.SeparatorText("Make a pack");
            ImGui.TextColored(Widgets.Dim, "dotnet run -c Release --project poc/Uo.Converter -- --uo <uo-install> --out <pack>");
        }
    }
}
