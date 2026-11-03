using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Land and static art (art/sprites.bin), with their tiledata, a hue preview and PNG export.</summary>
    internal sealed class ArtBrowser : AssetBrowser
    {
        private const int LandCount = ArtLoader.MAX_LAND_DATA_INDEX_COUNT;
        private const int ModeStatics = 0, ModeLand = 1;
        private static readonly string[] _modes = { "Statics", "Land" };

        private float _zoom = 2f;
        private int _hue;
        private bool _partialHue;
        private int _flagFilter = -1;
        private CpuImage _hued;
        private int _huedFor = -1;

        public ArtBrowser(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Art###art";
        public override string MenuName => "Art";
        public override string Icon => Icons.Image;

        private bool Land => Mode == ModeLand;
        private ArtLoader Loader => Context.Assets.Files.Arts;
        private ClassicUO.Renderer.Arts.Art Atlas => Context.Assets.Arts;
        private TileDataLoader Tiles => Context.Assets.Files.TileData;

        protected override string Kind => Land ? "Land tile" : "Static";
        protected override string[] Modes => _modes;
        protected override Parts InspectorParts => Parts.All;

        protected override int SlotCount => Context.Assets?.Arts == null ? 0 : Land ? LandCount : Math.Max(0, Loader.Count - LandCount);

        protected override bool Exists(int id) => Loader.Exists(Land ? id : id + LandCount);

        protected override string NameOf(int id)
        {
            if (Land)
            {
                return Tiles.LandData != null && id < Tiles.LandData.Length ? Tiles.LandData[id].Name : null;
            }

            return Tiles.StaticData != null && id < Tiles.StaticData.Length ? Tiles.StaticData[id].Name : null;
        }

        protected override string DetailOf(int id)
        {
            ref readonly var e = ref Loader.PackSpriteEntries[Land ? id : id + LandCount];

            return $"{e.OrigW} x {e.OrigH}";
        }

        protected override bool PassesFilter(int id) => _flagFilter < 0 || (Flags(id) & (1UL << _flagFilter)) != 0;

        private ulong Flags(int id)
        {
            if (Land)
            {
                return Tiles.LandData != null && id < Tiles.LandData.Length ? (ulong)Tiles.LandData[id].Flags : 0;
            }

            return Tiles.StaticData != null && id < Tiles.StaticData.Length ? (ulong)Tiles.StaticData[id].Flags : 0;
        }

        private bool RequestSprite(int id) => Land ? Atlas.RequestLand((uint)id) : Atlas.RequestArt((uint)id);

        private SpriteInfo GetSprite(int id) => Land ? Atlas.GetLand((uint)id) : Atlas.GetArt((uint)id);

        protected override void DrawThumbnail(ImDrawListPtr draw, int id, Vector2 min, Vector2 max)
        {
            if (RequestSprite(id))
            {
                Widgets.DrawSprite(draw, Context.Renderer, GetSprite(id), min, max, MathF.Max(1f, (max.X - min.X) / 44f));
            }
            else
            {
                Widgets.Pending(draw, min, max);
            }
        }

        /// <summary>Selects a static (or, with <paramref name="land"/>, a land tile).</summary>
        public void Select(int id, bool land) => SelectItem(id, land ? ModeLand : ModeStatics);

        public override void OnPackChanged()
        {
            base.OnPackChanged();
            _hued?.Clear();
            _huedFor = -1;
            BrushStatic = BrushLand = -1;
        }

        /// <summary>The static and the land tile last picked here: the map's Place and Paint brushes (-1 = none yet).</summary>
        public int BrushStatic { get; private set; } = -1;
        public int BrushLand { get; private set; } = -1;

        /// <summary>The kind picked last: the map's Draw tool places that static or paints that land.</summary>
        public bool BrushIsLand { get; private set; }

        public bool HasBrush => BrushIsLand ? BrushLand >= 0 : BrushStatic >= 0;

        /// <summary>Sets the brush without selecting in the browser (the map's Alt+click picker).</summary>
        public void SetBrush(int id, bool land)
        {
            BrushIsLand = land;

            if (land)
            {
                BrushLand = id;
            }
            else
            {
                BrushStatic = id;
            }
        }

        protected override void OnSelected(int id)
        {
            _huedFor = -1;
            SetBrush(id, Land);

            // picking art is picking the map's brush: the map switches to Draw
            Context.Get<MapPanel>()?.UseDrawTool();
        }

        protected override void OnModeChanged() => _huedFor = -1;

        protected override void OnActivated(int id) => Context.ShowTileData(id, Land);

        protected override void DrawFilters()
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f);

            if (TileFlags.Combo("##flag", ref _flagFilter))
            {
                Invalidate();
            }
        }

        protected override void DrawContextMenu(int id)
        {
            if (ImGui.MenuItem("Show tiledata"))
            {
                Context.ShowTileData(id, Land);
            }

            if (ImGui.MenuItem("Export PNG"))
            {
                Export(id);
            }
        }

        protected override void DrawPreview(int id)
        {
            var sprite = RequestSprite(id) ? GetSprite(id) : SpriteInfo.Empty;

            if (_hue != 0)
            {
                UpdateHued(id);
                sprite = _hued.Sprite;
            }

            SpritePreview(sprite, ref _zoom);

            ImGui.SetNextItemWidth(120f);

            if (ImGui.InputInt("hue", ref _hue, 0, 0))
            {
                _hue = Math.Clamp(_hue, 0, 0xFFFF);
                _huedFor = -1;
            }

            ImGui.SameLine();

            if (Ui.Toggle("partial", ref _partialHue))
            {
                _huedFor = -1;
            }

            Widgets.HelpMarker("A hue applied on the CPU. Partial hue recolours only grey pixels, as the client does for tiles with the PartialHue flag. Hue ids are 1-based.");

            if (_hue != 0)
            {
                ImGui.SameLine();

                if (Inspector.Link("show hue"))
                {
                    Context.ShowHue(_hue);
                }
            }
        }

        protected override void DrawProperties(int id)
        {
            ref readonly var entry = ref Loader.PackSpriteEntries[Land ? id : id + LandCount];

            if (!Inspector.BeginProperties())
            {
                return;
            }

            Inspector.Property("Size", $"{entry.OrigW} x {entry.OrigH}");
            TileDataBrowser.DrawProperties(Context, id, Land);
            Inspector.EndProperties();
        }

        protected override void DrawRelations(int id)
        {
            if (Land)
            {
                if (Tiles.LandData != null && id < Tiles.LandData.Length && Context.Assets.Files.Texmaps.Exists(Tiles.LandData[id].TexID)
                    && Inspector.BeginProperties("rel"))
                {
                    int tex = Tiles.LandData[id].TexID;

                    if (Inspector.PropertyLink("Texmap", Inspector.Id(tex)))
                    {
                        Context.ShowTexmap(tex);
                    }

                    Inspector.EndProperties();
                }

                return;
            }

            if (Tiles.StaticData != null && id < Tiles.StaticData.Length)
            {
                ref var s = ref Tiles.StaticData[id];

                if (s.IsWearable && s.AnimID != 0 && Inspector.BeginProperties("rel"))
                {
                    if (Inspector.PropertyLink("Animation", $"body {Inspector.Id(s.AnimID)}"))
                    {
                        Context.ShowBody(s.AnimID);
                    }

                    // paperdoll gumps: male at 50000 + anim, female at 60000 + anim
                    foreach (int gump in new[] { 50000 + s.AnimID, 60000 + s.AnimID })
                    {
                        if (Context.Assets.Files.Gumps.PixelCount((uint)gump) > 0 && Inspector.PropertyLink(gump < 60000 ? "Gump (male)" : "Gump (female)", Inspector.Id(gump)))
                        {
                            Context.ShowGump(gump);
                        }
                    }

                    Inspector.EndProperties();
                }
            }

            Inspector.LinkList("Used in multis", Context.Assets.Relations.MultisUsing(id), m => $"multi {Inspector.Id(m)}", m => Context.ShowMulti(m));
        }

        protected override void DrawActions(int id)
        {
            if (Ui.Button($"{Icons.Download}  Export PNG"))
            {
                Export(id);
            }

            ImGui.SameLine();

            if (Ui.Button($"{Icons.Table}  Tiledata"))
            {
                Context.ShowTileData(id, Land);
            }
        }

        private void UpdateHued(int id)
        {
            _hued ??= new CpuImage(Context.Device, Context.Renderer);

            if (_huedFor == id)
            {
                return;
            }

            _huedFor = id;
            var info = Loader.GetArt((uint)(Land ? id : id + LandCount));

            if (info.Pixels.IsEmpty)
            {
                _hued.Clear();

                return;
            }

            bool partial = _partialHue || (!Land && Tiles.StaticData != null && id < Tiles.StaticData.Length && Tiles.StaticData[id].IsPartialHue);
            Images.ApplyHue(info.Pixels, Context.Assets.Files.Hues.PackHues, _hue, partial);
            _hued.Set(info.Pixels, info.Width, info.Height);
        }

        private void Export(int id)
        {
            var info = Loader.GetArt((uint)(Land ? id : id + LandCount));

            if (info.Pixels.IsEmpty)
            {
                return;
            }

            if (_hue != 0)
            {
                Images.ApplyHue(info.Pixels, Context.Assets.Files.Hues.PackHues, _hue, _partialHue);
            }

            string name = Land ? $"land_0x{id:X4}" : $"static_0x{id:X4}";

            if (_hue != 0)
            {
                name += $"_hue{_hue}";
            }

            string path = Path.Combine(Context.Settings.ResolveExportDirectory(), name + ".png");
            Images.SavePng(Context.Device, path, info.Pixels, info.Width, info.Height);
            StudioLog.Info($"Exported {path}");
        }
    }

    /// <summary>The tiledata flag names (TileFlag bits), for filters and the details view.</summary>
    internal static class TileFlags
    {
        public static readonly string[] Names = BuildNames();

        private static string[] BuildNames()
        {
            var names = new string[64];

            for (int bit = 0; bit < 64; bit++)
            {
                var flag = (TileFlag)(1UL << bit);
                names[bit] = Enum.IsDefined(flag) ? flag.ToString() : $"bit {bit}";
            }

            return names;
        }

        public static string Describe(ulong flags)
        {
            if (flags == 0)
            {
                return "None";
            }

            var parts = new List<string>();

            for (int bit = 0; bit < 64; bit++)
            {
                if ((flags & (1UL << bit)) != 0)
                {
                    parts.Add(Names[bit]);
                }
            }

            return string.Join(", ", parts);
        }

        /// <summary>A "has flag" filter combo; -1 means any.</summary>
        public static bool Combo(string label, ref int bit)
        {
            bool changed = false;

            if (ImGui.BeginCombo(label, bit < 0 ? "any flag" : Names[bit]))
            {
                if (ImGui.Selectable("any flag", bit < 0))
                {
                    bit = -1;
                    changed = true;
                }

                for (int i = 0; i < 64; i++)
                {
                    if (Names[i].StartsWith("bit ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ImGui.Selectable(Names[i], bit == i))
                    {
                        bit = i;
                        changed = true;
                    }
                }

                ImGui.EndCombo();
            }

            return changed;
        }
    }
}
