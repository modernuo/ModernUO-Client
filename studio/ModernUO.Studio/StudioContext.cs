using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;
using ModernUO.Studio.Panels;

namespace ModernUO.Studio
{
    /// <summary>What every panel shares: the device, the ImGui renderer, the settings, the open pack and cross-panel navigation.</summary>
    internal sealed class StudioContext
    {
        private readonly List<Panel> _panels = new();
        private SoundEffectInstance _playing;
        private SoundEffect _playingEffect;

        public StudioContext(GraphicsDevice device, ImGuiRenderer renderer, StudioSettings settings)
        {
            Device = device;
            Renderer = renderer;
            Settings = settings;
        }

        public GraphicsDevice Device { get; }
        public ImGuiRenderer Renderer { get; }
        public StudioSettings Settings { get; }
        public StudioAssets Assets { get; private set; }
        public IReadOnlyList<Panel> Panels => _panels;

        public void Add(Panel panel) => _panels.Add(panel);

        public T Get<T>() where T : Panel
        {
            foreach (var panel in _panels)
            {
                if (panel is T t)
                {
                    return t;
                }
            }

            return null;
        }

        public bool OpenPack(string root)
        {
            StudioAssets assets;

            try
            {
                assets = StudioAssets.Open(root, Device);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                StudioLog.Error($"Could not open pack '{root}': {ex.Message}");

                return false;
            }

            ClosePack();
            Assets = assets;

            // after an edit, undo or redo the picked tile shows its new content
            assets.Edits.Changed += () =>
            {
                if (SelectedTile is { } t)
                {
                    int hovered = HoveredStatic;
                    SelectTile(t.Facet, t.X, t.Y);
                    HoveredStatic = hovered < SelectedTile.Statics.Count ? hovered : -1;
                }
            };
            Settings.AddRecent(root);
            Settings.Save();

            int failed = assets.Results.Count(r => r.Error != null);
            StudioLog.Info($"Opened pack {root} (from client {assets.Manifest.SourceClientVersion}, {assets.Results.Count - failed} sections loaded, {failed} failed)");

            foreach (var panel in _panels)
            {
                panel.OnPackChanged();
            }

            return true;
        }

        public void ClosePack()
        {
            if (Assets == null)
            {
                return;
            }

            StopSound();

            foreach (var panel in _panels)
            {
                panel.OnPackChanged();
            }

            ResetMap();

            try
            {
                Assets.Dispose();
            }
            catch (Exception ex)
            {
                // a loader that never loaded can fail its own teardown; the pack is closed either way
                StudioLog.Warn($"Closing the pack: {ex.Message}");
            }

            Assets = null;
        }

        public void PlaySound(byte[] pcm)
        {
            StopSound();

            if (pcm == null || pcm.Length < 2)
            {
                return;
            }

            try
            {
                _playingEffect = new SoundEffect(pcm, 22050, AudioChannels.Mono);
                _playing = _playingEffect.CreateInstance();
                _playing.Play();
            }
            catch (Exception ex) when (ex is NoAudioHardwareException or InvalidOperationException or ArgumentException)
            {
                StudioLog.Warn($"Sound playback failed: {ex.Message}");
            }
        }

        public bool IsSoundPlaying => _playing is { State: SoundState.Playing };

        public void StopSound()
        {
            _playing?.Stop();
            _playing?.Dispose();
            _playing = null;
            _playingEffect?.Dispose();
            _playingEffect = null;
        }

        public void ShowArt(int id, bool land) => Get<ArtBrowser>()?.Select(id, land);

        public void ShowTileData(int id, bool land) => Get<TileDataBrowser>()?.Select(id, land);

        public void ShowHue(int hue) => Get<HueBrowser>()?.Select(hue);

        public void ShowGump(int id) => Get<GumpBrowser>()?.Select(id);

        // ---- the map

        private MapWorld _mapWorld;
        private string _mapError;

        /// <summary>The GPU world pass for the open pack, created on first use; null with <see cref="MapError"/> when it can't run.</summary>
        public MapWorld MapWorld
        {
            get
            {
                if (_mapWorld == null && _mapError == null && Assets != null)
                {
                    _mapWorld = MapWorld.TryCreate(Device, Assets, out _mapError);
                    _mapWorld?.AttachEdits(Assets.Edits);

                    if (_mapError != null)
                    {
                        StudioLog.Error($"Map: {_mapError}");
                    }
                }

                return _mapWorld;
            }
        }

        public string MapError => _mapError;

        /// <summary>The tile the map selected (the Tile inspector shows it); null before the first pick.</summary>
        public MapTile SelectedTile { get; private set; }

        /// <summary>The selected tile's static the Tile inspector is hovering (the map highlights it); -1 for none.</summary>
        public int HoveredStatic { get; set; } = -1;

        public void SelectTile(int facet, int x, int y)
        {
            var tile = new MapTile();

            if (Assets != null && MapData.TryGetTile(Assets, facet, x, y, tile))
            {
                SelectedTile = tile;
                HoveredStatic = -1;
            }
        }

        public void ShowOnMap(int facet, int x, int y) => Get<MapPanel>()?.CenterOn(facet, x, y, select: true);

        private void ResetMap()
        {
            _mapWorld?.Dispose();
            _mapWorld = null;
            _mapError = null;
            SelectedTile = null;
            HoveredStatic = -1;
        }

        /// <summary>After ImGui built the frame, before FNA draws it: the map's world pass draws into its target.</summary>
        public void RenderWorld() => Get<MapPanel>()?.RenderWorld();

        public void ShowTexmap(int id) => Get<TexmapBrowser>()?.SelectItem(id);

        public void ShowBody(int id) => Get<AnimationBrowser>()?.SelectItem(id);

        public void ShowMulti(int id) => Get<MultiBrowser>()?.SelectItem(id);
    }
}
