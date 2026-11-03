using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Gump images (gumps/gumps.bin), with hue preview and PNG export.</summary>
    internal sealed class GumpBrowser : AssetBrowser
    {
        private float _zoom = 1f;
        private int _hue;
        private bool _partialHue;
        private CpuImage _hued;
        private int _huedFor = -1;

        public GumpBrowser(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Gumps###gumps";
        public override string MenuName => "Gumps";
        public override string Icon => Icons.Panels;

        private GumpsLoader Loader => Context.Assets.Files.Gumps;

        protected override string Kind => "Gump";
        protected override float DefaultCell => 80f;
        protected override string SearchHint => "gump id (0x7D0, 2000)";

        protected override int SlotCount => Context.Assets?.Gumps == null ? 0 : Loader.Count;

        protected override bool Exists(int id) => Loader.PixelCount((uint)id) > 0;

        protected override string DetailOf(int id) => Loader.TryGetSize((uint)id, out int w, out int h) ? $"{w} x {h}" : null;

        protected override void DrawThumbnail(ImDrawListPtr draw, int id, Vector2 min, Vector2 max)
        {
            if (Context.Assets.Gumps.Request((uint)id))
            {
                // gumps are large: shrink to fit, never enlarge past 2x
                Widgets.DrawSprite(draw, Context.Renderer, Context.Assets.Gumps.GetGump((uint)id), min, max, 2f);
            }
            else
            {
                Widgets.Pending(draw, min, max);
            }
        }

        public void Select(int id) => SelectItem(id);

        protected override void OnSelected(int id) => _huedFor = -1;

        public override void OnPackChanged()
        {
            base.OnPackChanged();
            _hued?.Clear();
            _huedFor = -1;
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
            var sprite = Context.Assets.Gumps.Request((uint)id) ? Context.Assets.Gumps.GetGump((uint)id) : SpriteInfo.Empty;

            if (_hue != 0)
            {
                UpdateHued(id);
                sprite = _hued.Sprite;
            }

            SpritePreview(sprite, ref _zoom, 320f);

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
        }

        protected override void DrawProperties(int id)
        {
            if (Inspector.BeginProperties())
            {
                if (Loader.TryGetSize((uint)id, out int w, out int h))
                {
                    Inspector.Property("Size", $"{w} x {h}");
                }

                // paperdoll gumps of wearables: 50000 + anim id (male), 60000 + anim id (female)
                if (id >= 50000 && id < 70000)
                {
                    Inspector.Property("Paperdoll", $"{(id < 60000 ? "male" : "female")}, animation 0x{id % 10000:X4}");
                }

                Inspector.EndProperties();
            }
        }

        protected override void DrawActions(int id)
        {
            if (Ui.Button($"{Icons.Download}  Export PNG"))
            {
                Export(id);
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
            var info = Loader.GetGump((uint)id);

            if (info.Pixels.IsEmpty)
            {
                _hued.Clear();

                return;
            }

            Images.ApplyHue(info.Pixels, Context.Assets.Files.Hues.PackHues, _hue, _partialHue);
            _hued.Set(info.Pixels, info.Width, info.Height);
        }

        private void Export(int id)
        {
            var info = Loader.GetGump((uint)id);

            if (info.Pixels.IsEmpty)
            {
                return;
            }

            if (_hue != 0)
            {
                Images.ApplyHue(info.Pixels, Context.Assets.Files.Hues.PackHues, _hue, _partialHue);
            }

            string path = Path.Combine(Context.Settings.ResolveExportDirectory(), _hue != 0 ? $"gump_0x{id:X4}_hue{_hue}.png" : $"gump_0x{id:X4}.png");
            Images.SavePng(Context.Device, path, info.Pixels, info.Width, info.Height);
            StudioLog.Info($"Exported {path}");
        }
    }
}
