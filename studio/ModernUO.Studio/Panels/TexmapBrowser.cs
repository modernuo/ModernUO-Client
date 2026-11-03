using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Land textures (tex/texmaps.bin) and the land tiles that use each one.</summary>
    internal sealed class TexmapBrowser : SpriteGridBrowser
    {
        private float _zoom = 2f;

        public TexmapBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Texmaps###texmaps";
        public override string MenuName => "Texmaps";

        private TexmapsLoader Loader => Context.Assets.Files.Texmaps;

        protected override int SlotCount => Context.Assets?.Texmaps == null ? 0 : Loader.Count;

        protected override bool Exists(int id) => Loader.Exists(id);

        protected override bool Request(int id) => Context.Assets.Texmaps.Request((uint)id);

        protected override SpriteInfo Get(int id) => Context.Assets.Texmaps.GetTexmap((uint)id);

        protected override void DrawContextMenu(int id)
        {
            if (ImGui.MenuItem("Export PNG"))
            {
                Export(id);
            }
        }

        protected override void DrawDetails(int id)
        {
            Widgets.CopyableId("Texmap", id);
            ref readonly var entry = ref Loader.PackSpriteEntries[id];
            ImGui.TextUnformatted($"Size: {entry.OrigW} x {entry.OrigH}");

            Preview(Context, Request(id) ? Get(id) : SpriteInfo.Empty, ref _zoom);

            if (ImGui.Button("Export PNG"))
            {
                Export(id);
            }

            ImGui.Separator();
            ImGui.TextColored(Widgets.Dim, "Land tiles using it");

            var land = Context.Assets.Files.TileData.LandData;

            if (land == null)
            {
                return;
            }

            if (ImGui.BeginChild("users"))
            {
                for (int i = 0; i < land.Length; i++)
                {
                    if (land[i].TexID == id && ImGui.Selectable($"0x{i:X4}  {land[i].Name}"))
                    {
                        Context.ShowArt(i, true);
                    }
                }
            }

            ImGui.EndChild();
        }

        private void Export(int id)
        {
            var info = Loader.GetTexmap((uint)id);

            if (info.Pixels.IsEmpty)
            {
                return;
            }

            string path = Path.Combine(Context.Settings.ResolveExportDirectory(), $"texmap_0x{id:X4}.png");
            Images.SavePng(Context.Device, path, info.Pixels, info.Width, info.Height);
            StudioLog.Info($"Exported {path}");
        }
    }
}
