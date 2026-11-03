using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>tiledata.bin as a table: every land and static entry with its flags, filterable by name, id and flag.</summary>
    internal sealed class TileDataBrowser : AssetBrowser
    {
        private const int ModeStatics = 0, ModeLand = 1;
        private static readonly string[] _modes = { "Statics", "Land" };

        private int _flagFilter = -1;
        private float _zoom = 2f;

        public TileDataBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "TileData###tiledata";
        public override string MenuName => "TileData";
        public override string Icon => Icons.Table;

        private bool Land => Mode == ModeLand;
        private TileDataLoader Tiles => Context.Assets.Files.TileData;

        protected override string Kind => Land ? "Land tile" : "Static";
        protected override string[] Modes => _modes;
        protected override bool HasGrid => false;
        protected override bool HasThumbnails => true;
        protected override float DefaultCell => 48f;
        protected override Parts InspectorParts => Parts.All;

        protected override int SlotCount => Context.Assets == null || Tiles.StaticData == null ? 0 : Land ? Tiles.LandData.Length : Tiles.StaticData.Length;

        protected override string NameOf(int id) => Land ? Tiles.LandData[id].Name : Tiles.StaticData[id].Name;

        private ulong Flags(int id) => Land ? (ulong)Tiles.LandData[id].Flags : (ulong)Tiles.StaticData[id].Flags;

        protected override bool PassesFilter(int id) => _flagFilter < 0 || (Flags(id) & (1UL << _flagFilter)) != 0;

        protected override int ExtraColumns => Land ? 2 : 8;

        protected override void SetupExtraColumns()
        {
            if (Land)
            {
                ImGui.TableSetupColumn("TexId", ImGuiTableColumnFlags.WidthFixed, 60f);
            }
            else
            {
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Height", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Layer", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("Anim", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Hue", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("Light", ImGuiTableColumnFlags.WidthFixed, 45f);
            }

            ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.WidthStretch);
        }

        protected override void DrawExtraColumns(int id)
        {
            if (Land)
            {
                ref var t = ref Tiles.LandData[id];
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"0x{t.TexID:X4}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(TileFlags.Describe((ulong)t.Flags));

                return;
            }

            ref var s = ref Tiles.StaticData[id];
            Cell(s.Weight.ToString());
            Cell(s.Height.ToString());
            Cell(s.Layer.ToString());
            Cell(s.Count.ToString());
            Cell(s.AnimID == 0 ? "" : $"0x{s.AnimID:X4}");
            Cell(s.Hue == 0 ? "" : s.Hue.ToString());
            Cell(s.LightIndex == 0 ? "" : s.LightIndex.ToString());
            Cell(TileFlags.Describe((ulong)s.Flags));
        }

        private static void Cell(string text)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(text);
        }

        private bool TryGetArt(int id, out SpriteInfo sprite)
        {
            sprite = SpriteInfo.Empty;
            var art = Context.Assets.Arts;

            if (art == null || !Context.Assets.Files.Arts.Exists(Land ? id : id + ArtLoader.MAX_LAND_DATA_INDEX_COUNT))
            {
                return false;
            }

            if (!(Land ? art.RequestLand((uint)id) : art.RequestArt((uint)id)))
            {
                return false;
            }

            sprite = Land ? art.GetLand((uint)id) : art.GetArt((uint)id);

            return true;
        }

        protected override void DrawThumbnail(ImDrawListPtr draw, int id, Vector2 min, Vector2 max)
        {
            if (TryGetArt(id, out var sprite))
            {
                Widgets.DrawSprite(draw, Context.Renderer, sprite, min, max, 1f);
            }
        }

        public void Select(int id, bool land) => SelectItem(id, land ? ModeLand : ModeStatics);

        protected override void OnActivated(int id) => Context.ShowArt(id, Land);

        protected override void DrawFilters()
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160f);

            if (TileFlags.Combo("##flag", ref _flagFilter))
            {
                Invalidate();
            }
        }

        protected override void DrawContextMenu(int id)
        {
            if (ImGui.MenuItem("Show art"))
            {
                Context.ShowArt(id, Land);
            }
        }

        protected override void DrawPreview(int id)
        {
            if (TryGetArt(id, out var sprite))
            {
                SpritePreview(sprite, ref _zoom, 160f);
            }
            else
            {
                ImGui.TextColored(Widgets.Dim, "No art for this entry.");
            }
        }

        protected override void DrawProperties(int id)
        {
            if (Inspector.BeginProperties())
            {
                DrawProperties(Context, id, Land);
                Inspector.EndProperties();
            }
        }

        protected override void DrawRelations(int id)
        {
            if (Inspector.Link(Land ? $"land art {Inspector.Id(id)}" : $"static art {Inspector.Id(id)}"))
            {
                Context.ShowArt(id, Land);
            }
        }

        protected override void DrawActions(int id)
        {
            if (Ui.Button($"{Icons.Image}  Show art"))
            {
                Context.ShowArt(id, Land);
            }
        }

        /// <summary>The tiledata rows of one entry, inside an open <see cref="Inspector.BeginProperties"/> table (also used by Art).</summary>
        public static void DrawProperties(StudioContext context, int id, bool land)
        {
            var tiles = context.Assets.Files.TileData;

            if (land)
            {
                if (tiles.LandData == null || id >= tiles.LandData.Length)
                {
                    Inspector.Property("Tiledata", "none");

                    return;
                }

                ref var t = ref tiles.LandData[id];
                Inspector.Property("Name", t.Name);
                Inspector.Property("Texmap", $"0x{t.TexID:X4}");
                Inspector.Property("Flags", FlagLines((ulong)t.Flags));

                return;
            }

            if (tiles.StaticData == null || id >= tiles.StaticData.Length)
            {
                Inspector.Property("Tiledata", "none");

                return;
            }

            ref var s = ref tiles.StaticData[id];
            Inspector.Property("Name", s.Name);
            Inspector.Property("Weight", s.Weight.ToString());
            Inspector.Property("Height", s.Height.ToString());
            Inspector.Property("Layer", s.Layer.ToString());
            Inspector.Property("Count", s.Count.ToString());
            Inspector.Property("Anim", $"0x{s.AnimID:X4}");
            Inspector.Property("Hue", s.Hue.ToString());
            Inspector.Property("Light", s.LightIndex.ToString());
            Inspector.Property("Flags", FlagLines((ulong)s.Flags));
        }

        private static string FlagLines(ulong flags) => flags == 0 ? "None" : TileFlags.Describe(flags).Replace(", ", "\n");
    }
}
