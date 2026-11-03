using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Land and static art (art/sprites.bin), with their tiledata, a hue preview and PNG export.</summary>
    internal sealed class ArtBrowser : SpriteGridBrowser
    {
        private const int LandCount = ArtLoader.MAX_LAND_DATA_INDEX_COUNT;

        private bool _land;
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

        private ArtLoader Loader => Context.Assets.Files.Arts;
        private ClassicUO.Renderer.Arts.Art Atlas => Context.Assets.Arts;

        protected override int SlotCount => Context.Assets?.Arts == null ? 0 : _land ? LandCount : Math.Max(0, Loader.Count - LandCount);

        protected override bool Exists(int id) => Loader.Exists(_land ? id : id + LandCount);

        protected override bool Request(int id) => _land ? Atlas.RequestLand((uint)id) : Atlas.RequestArt((uint)id);

        protected override SpriteInfo Get(int id) => _land ? Atlas.GetLand((uint)id) : Atlas.GetArt((uint)id);

        protected override string NameOf(int id)
        {
            var tiles = Context.Assets.Files.TileData;

            if (_land)
            {
                return id < tiles.LandData.Length ? tiles.LandData[id].Name : null;
            }

            return id < tiles.StaticData.Length ? tiles.StaticData[id].Name : null;
        }

        protected override bool PassesFilter(int id)
        {
            if (_flagFilter < 0)
            {
                return true;
            }

            var tiles = Context.Assets.Files.TileData;
            ulong flag = 1UL << _flagFilter;
            ulong flags = _land
                ? id < tiles.LandData.Length ? (ulong)tiles.LandData[id].Flags : 0
                : id < tiles.StaticData.Length ? (ulong)tiles.StaticData[id].Flags : 0;

            return (flags & flag) != 0;
        }

        public void Select(int id, bool land)
        {
            if (_land != land)
            {
                _land = land;
                Invalidate();
            }

            SelectId(id);
        }

        public override void OnPackChanged()
        {
            base.OnPackChanged();
            _hued?.Clear();
            _huedFor = -1;
        }

        protected override void OnSelected(int id) => _huedFor = -1;

        protected override void DrawToolbarExtras()
        {
            ImGui.SameLine();

            if (ImGui.RadioButton("Statics", !_land) && _land)
            {
                _land = false;
                Invalidate();
                OnPackChanged();
            }

            ImGui.SameLine();

            if (ImGui.RadioButton("Land", _land) && !_land)
            {
                _land = true;
                Invalidate();
                OnPackChanged();
            }

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
                Context.ShowTileData(id, _land);
            }

            if (ImGui.MenuItem("Export PNG"))
            {
                Export(id);
            }
        }

        protected override void DrawDetails(int id)
        {
            ImGui.TextUnformatted(_land ? "Land tile" : "Static");
            Widgets.CopyableId("Id", id);

            int artIndex = _land ? id : id + LandCount;
            ref readonly var entry = ref Loader.PackSpriteEntries[artIndex];
            ImGui.TextUnformatted($"Size: {entry.OrigW} x {entry.OrigH}");

            var sprite = Request(id) ? Get(id) : SpriteInfo.Empty;

            if (_hue != 0)
            {
                UpdateHued(id, artIndex);
                sprite = _hued.Sprite;
            }

            Preview(Context, sprite, ref _zoom);

            ImGui.SetNextItemWidth(120f);

            if (ImGui.InputInt("hue", ref _hue))
            {
                _hue = Math.Clamp(_hue, 0, 0xFFFF);
                _huedFor = -1;
            }

            ImGui.SameLine();

            if (ImGui.Checkbox("partial", ref _partialHue))
            {
                _huedFor = -1;
            }

            Widgets.HelpMarker("Preview the tile with a hue applied on the CPU. Partial hue recolours only grey pixels, as the client does for tiles with the PartialHue flag. Hue ids are 1-based.");

            if (_hue != 0)
            {
                ImGui.SameLine();

                if (ImGui.SmallButton("show hue"))
                {
                    Context.ShowHue(_hue);
                }
            }

            if (ImGui.Button("Export PNG"))
            {
                Export(id);
            }

            ImGui.SameLine();

            if (ImGui.Button("Tiledata"))
            {
                Context.ShowTileData(id, _land);
            }

            ImGui.Separator();
            TileDataBrowser.DrawFields(Context, id, _land);
        }

        private void UpdateHued(int id, int artIndex)
        {
            _hued ??= new CpuImage(Context.Device, Context.Renderer);

            if (_huedFor == id)
            {
                return;
            }

            _huedFor = id;
            var info = Loader.GetArt((uint)artIndex);

            if (info.Pixels.IsEmpty)
            {
                _hued.Clear();

                return;
            }

            bool partial = _partialHue || (!_land && id < Context.Assets.Files.TileData.StaticData.Length
                && Context.Assets.Files.TileData.StaticData[id].IsPartialHue);
            Images.ApplyHue(info.Pixels, Context.Assets.Files.Hues.PackHues, _hue, partial);
            _hued.Set(info.Pixels, info.Width, info.Height);
        }

        private void Export(int id)
        {
            int artIndex = _land ? id : id + LandCount;
            var info = Loader.GetArt((uint)artIndex);

            if (info.Pixels.IsEmpty)
            {
                return;
            }

            if (_hue != 0)
            {
                Images.ApplyHue(info.Pixels, Context.Assets.Files.Hues.PackHues, _hue, _partialHue);
            }

            string name = _land ? $"land_0x{id:X4}" : $"static_0x{id:X4}";

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
