using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Land textures (tex/texmaps.bin) and the land tiles that use each one.</summary>
    internal sealed class TexmapBrowser : AssetBrowser
    {
        private float _zoom = 2f;

        public TexmapBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Texmaps###texmaps";
        public override string MenuName => "Texmaps";
        public override string Icon => Icons.BrickWall;

        private TexmapsLoader Loader => Context.Assets.Files.Texmaps;

        protected override string Kind => "Texmap";
        protected override string SearchHint => "texmap id";
        protected override Parts InspectorParts => Parts.All;

        protected override int SlotCount => Context.Assets?.Texmaps == null ? 0 : Loader.Count;

        protected override bool Exists(int id) => Loader.Exists(id);

        protected override string DetailOf(int id)
        {
            ref readonly var e = ref Loader.PackSpriteEntries[id];

            return $"{e.OrigW} x {e.OrigH}";
        }

        protected override void DrawThumbnail(ImDrawListPtr draw, int id, Vector2 min, Vector2 max)
        {
            if (Context.Assets.Texmaps.Request((uint)id))
            {
                Widgets.DrawSprite(draw, Context.Renderer, Context.Assets.Texmaps.GetTexmap((uint)id), min, max, 2f);
            }
            else
            {
                Widgets.Pending(draw, min, max);
            }
        }

        protected override void DrawContextMenu(int id)
        {
            if (ImGui.MenuItem("Export PNG"))
            {
                Export(id);
            }
        }

        protected override void DrawPreview(int id)
        {
            SpritePreview(Context.Assets.Texmaps.Request((uint)id) ? Context.Assets.Texmaps.GetTexmap((uint)id) : SpriteInfo.Empty, ref _zoom);
        }

        protected override void DrawProperties(int id)
        {
            ref readonly var e = ref Loader.PackSpriteEntries[id];

            if (Inspector.BeginProperties())
            {
                Inspector.Property("Size", $"{e.OrigW} x {e.OrigH}");
                Inspector.EndProperties();
            }
        }

        protected override void DrawRelations(int id)
        {
            var land = Context.Assets.Files.TileData.LandData;
            Inspector.LinkList("Land tiles using it", Context.Assets.Relations.LandUsing(id),
                i => $"{Inspector.Id(i)}  {(land != null && i < land.Length ? land[i].Name : "")}", i => Context.ShowArt(i, true));
        }

        protected override void DrawActions(int id)
        {
            if (Ui.Button($"{Icons.Download}  Export PNG"))
            {
                Export(id);
            }
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
