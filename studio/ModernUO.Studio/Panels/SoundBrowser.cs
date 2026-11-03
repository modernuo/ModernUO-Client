using System.Numerics;
using ClassicUO.Assets;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Sound effects (audio/sounds.bin, 22,050 Hz mono PCM) and music (audio/music.bin, MP3): play and export.</summary>
    internal sealed class SoundBrowser : AssetBrowser
    {
        private const int ModeSounds = 0;
        private static readonly string[] _modes = { "Sounds", "Music" };
        private const int MaxMusic = 1024;

        private readonly Dictionary<int, long> _sounds = new();
        private readonly Dictionary<int, (string Name, bool Loop)> _music = new();
        private readonly Dictionary<int, string> _names = new();
        private bool _scanned;
        private int _waveFor = -1;
        private float[] _wave = Array.Empty<float>();

        public SoundBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Sounds###sounds";
        public override string MenuName => "Sounds & Music";

        private SoundsLoader Loader => Context.Assets.Files.Sounds;
        private bool Music => Mode != ModeSounds;

        protected override string Kind => Music ? "Music" : "Sound";
        protected override string[] Modes => _modes;
        protected override bool HasGrid => false;
        protected override string SearchHint => "id or name";
        protected override int ExtraColumns => 1;

        protected override int SlotCount
        {
            get
            {
                if (Context.Assets == null)
                {
                    return 0;
                }

                Scan();

                return Music ? MaxMusic : SoundsLoader.MAX_SOUND_DATA_INDEX_COUNT;
            }
        }

        protected override bool Exists(int id) => Music ? _music.ContainsKey(id) : _sounds.ContainsKey(id);

        protected override string FormatId(int id) => Music ? id.ToString() : Inspector.Id(id);

        // sound names live in the records, read with the PCM: fetched once per id, for the visible rows or a text search
        protected override string NameOf(int id)
        {
            if (Music)
            {
                return _music.TryGetValue(id, out var m) ? m.Name : null;
            }

            if (!_names.TryGetValue(id, out string name))
            {
                Loader.TryGetSound(id, out _, out name);
                _names[id] = name ??= "";
            }

            return name;
        }

        protected override void SetupExtraColumns() => ImGui.TableSetupColumn(Music ? "Loop" : "Length", ImGuiTableColumnFlags.WidthStretch);

        protected override void DrawExtraColumns(int id)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Music ? (_music[id].Loop ? "loops" : "") : $"{_sounds[id] / 44100.0:0.00} s");
        }

        private void Scan()
        {
            if (_scanned)
            {
                return;
            }

            _scanned = true;

            if (Loader.HasPackSounds)
            {
                for (int id = 0; id < SoundsLoader.MAX_SOUND_DATA_INDEX_COUNT; id++)
                {
                    if (Loader.TryGetSoundSource(id, out _, out long bytes) && bytes > 0)
                    {
                        _sounds[id] = bytes;
                    }
                }
            }

            if (Loader.HasPackMusic)
            {
                for (int id = 0; id < MaxMusic; id++)
                {
                    if (Loader.TryGetMusic(id, out string name, out bool loop, out _))
                    {
                        _music[id] = (name, loop);
                    }
                }
            }
        }

        public override void OnPackChanged()
        {
            base.OnPackChanged();
            _scanned = false;
            _sounds.Clear();
            _music.Clear();
            _names.Clear();
            _waveFor = -1;
        }

        protected override void OnModeChanged() => _waveFor = -1;

        protected override void OnActivated(int id)
        {
            if (!Music)
            {
                Play(id);
            }
        }

        protected override void DrawContextMenu(int id)
        {
            if (!Music && ImGui.MenuItem("Play"))
            {
                Play(id);
            }

            if (ImGui.MenuItem(Music ? "Export MP3" : "Export WAV"))
            {
                Export(id);
            }
        }

        protected override void DrawPreview(int id)
        {
            if (Music)
            {
                ImGui.TextColored(Widgets.Dim, "Music is stored as the original MP3 bytes; export writes them unchanged.");

                return;
            }

            UpdateWave(id);

            var draw = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), 120f);
            ImGui.Dummy(size);
            draw.AddRectFilled(min, min + size, Widgets.Col(0.08f, 0.08f, 0.09f));
            float mid = min.Y + size.Y * 0.5f;
            draw.AddLine(new Vector2(min.X, mid), new Vector2(min.X + size.X, mid), Widgets.Col(0.3f, 0.3f, 0.3f));

            // one column per pixel: the peak of the samples it covers
            int columns = Math.Max(1, (int)size.X);
            uint color = ImGui.ColorConvertFloat4ToU32(Inspector.LinkColor);

            for (int x = 0; x < columns && _wave.Length > 0; x++)
            {
                float peak = _wave[(int)((long)x * _wave.Length / columns)];
                float h = peak * size.Y * 0.5f;
                draw.AddLine(new Vector2(min.X + x, mid - h), new Vector2(min.X + x, mid + h), color);
            }

            ImGui.TextColored(Widgets.Dim, "Enter or double-click plays.");
        }

        private void UpdateWave(int id)
        {
            if (_waveFor == id)
            {
                return;
            }

            _waveFor = id;
            _wave = Array.Empty<float>();

            if (!Loader.TryGetSound(id, out byte[] pcm, out _) || pcm.Length < 2)
            {
                return;
            }

            // peaks per bucket of samples, enough for any inspector width
            const int buckets = 2048;
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, pcm.Length & ~1));
            int count = Math.Min(buckets, samples.Length);
            _wave = new float[count];

            for (int b = 0; b < count; b++)
            {
                int start = (int)((long)b * samples.Length / count), end = (int)((long)(b + 1) * samples.Length / count);
                int peak = 0;

                for (int i = start; i < end; i++)
                {
                    peak = Math.Max(peak, Math.Abs((int)samples[i]));
                }

                _wave[b] = peak / 32768f;
            }
        }

        protected override void DrawProperties(int id)
        {
            if (!Inspector.BeginProperties())
            {
                return;
            }

            if (Music)
            {
                var m = _music[id];
                Inspector.Property("Name", m.Name);
                Inspector.Property("Loops", m.Loop ? "yes" : "no");
            }
            else
            {
                long bytes = _sounds[id];
                Inspector.Property("Length", $"{bytes / 44100.0:0.00} s");
                Inspector.Property("PCM", $"{bytes / 1024.0:0.0} KB, 22,050 Hz mono 16-bit");
            }

            Inspector.EndProperties();
        }

        protected override void DrawActions(int id)
        {
            if (!Music)
            {
                // one button that plays or stops; "###play" keeps its id while the label changes
                if (ImGui.Button(Context.IsSoundPlaying ? "Stop###play" : "Play###play"))
                {
                    if (Context.IsSoundPlaying)
                    {
                        Context.StopSound();
                    }
                    else
                    {
                        Play(id);
                    }
                }

                ImGui.SameLine();
            }

            if (ImGui.Button(Music ? "Export MP3" : "Export WAV"))
            {
                Export(id);
            }
        }

        private void Play(int id)
        {
            if (Loader.TryGetSound(id, out byte[] pcm, out _))
            {
                Context.PlaySound(pcm);
            }
        }

        private void Export(int id)
        {
            string dir = Context.Settings.ResolveExportDirectory();

            if (Music)
            {
                if (!Loader.TryGetMusic(id, out string name, out _, out Func<Stream> open))
                {
                    return;
                }

                string path = Path.Combine(dir, $"music_{id}_{Sanitize(name ?? "")}.mp3");

                using (var src = open())
                using (var dst = File.Create(path))
                {
                    src.CopyTo(dst);
                }

                StudioLog.Info($"Exported {path}");

                return;
            }

            if (!Loader.TryGetSound(id, out byte[] pcm, out string soundName))
            {
                return;
            }

            string wav = Path.Combine(dir, $"sound_0x{id:X4}{(string.IsNullOrEmpty(soundName) ? "" : "_" + Sanitize(soundName))}.wav");
            Images.SaveWav(wav, pcm);
            StudioLog.Info($"Exported {wav}");
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name;
        }
    }
}
