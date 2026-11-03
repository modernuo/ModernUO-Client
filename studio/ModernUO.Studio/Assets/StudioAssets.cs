using System.Diagnostics;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;

namespace ModernUO.Studio.Assets
{
    /// <summary>
    /// One open pack: the client's loaders (ClassicUO.Assets) for the data and the client's atlases (ClassicUO.Renderer) for
    /// what is drawn. Studio reads only the pack; a loader that fails is reported and its browsers show nothing.
    /// </summary>
    internal sealed class StudioAssets : IDisposable
    {
        public sealed record LoadResult(string Name, double Milliseconds, string Error);

        private readonly List<LoadResult> _results = new();

        private StudioAssets(string root, PackAssets pack, UOFileManager files)
        {
            Root = root;
            Pack = pack;
            Files = files;
        }

        public string Root { get; }
        public PackAssets Pack { get; }
        public Manifest Manifest => Pack.Manifest;
        public UOFileManager Files { get; }
        public IReadOnlyList<LoadResult> Results => _results;

        public ClassicUO.Renderer.Arts.Art Arts { get; private set; }
        public ClassicUO.Renderer.Gumps.Gump Gumps { get; private set; }
        public ClassicUO.Renderer.Texmaps.Texmap Texmaps { get; private set; }
        public ClassicUO.Renderer.Animations.Animations Animations { get; private set; }

        /// <summary>hues_raw.bin as stored (names and TableStart/End included), for the hue browser.</summary>
        public HueEntry[] HueEntries { get; private set; } = Array.Empty<HueEntry>();

        public bool Loaded(string name) => _results.Exists(r => r.Name == name && r.Error == null);

        public static StudioAssets Open(string root, GraphicsDevice device)
        {
            var pack = PackAssets.Open(root);

            // The pack names the client it was converted from; the version only picks the loaders' layout rules. The pack
            // root stands in for a UO folder: Studio never opens MUL/UOP files, so every fallback path finds nothing.
            if (!ClientVersionHelper.IsClientVersionValid(pack.Manifest.SourceClientVersion, out ClientVersion version))
            {
                version = ClientVersion.CV_7010400;
            }

            var assets = new StudioAssets(root, pack, new UOFileManager(version, root, pack));
            assets.Load(device);

            return assets;
        }

        private void Load(GraphicsDevice device)
        {
            var f = Files;

            // the order of UOFileManager.Load, without the loaders that read only MUL files (skills, professions, speech,
            // string dictionary) or are not browsed yet (maps, multimaps)
            Run("anims", f.Animations.Load);
            Run("animdata", f.AnimData.Load);
            Run("art", f.Arts.Load);
            Run("cliloc", () => f.Clilocs.Load("ENU"));
            Run("gumps", f.Gumps.Load);
            Run("fonts", f.Fonts.Load);
            Run("hues", f.Hues.Load);
            Run("hues names", () => HueEntries = HueRawFile.Read(Pack.PathOf(TableConst.HueRawFile)));
            Run("tiledata", f.TileData.Load);
            Run("multis", f.Multis.Load);
            Run("texmaps", f.Texmaps.Load);
            Run("lights", f.Lights.Load);
            Run("sounds", f.Sounds.Load);
            Run("tileart", f.TileArt.Load);

            if (Loaded("art") && Loaded("hues"))
            {
                Run("art atlas", () => Arts = new ClassicUO.Renderer.Arts.Art(f.Arts, f.Hues, device));
            }

            if (Loaded("gumps"))
            {
                Run("gump atlas", () => Gumps = new ClassicUO.Renderer.Gumps.Gump(f.Gumps, device));
            }

            if (Loaded("texmaps"))
            {
                Run("texmap atlas", () => Texmaps = new ClassicUO.Renderer.Texmaps.Texmap(f.Texmaps, device));
            }

            if (Loaded("anims"))
            {
                Run("anim atlas", () => Animations = new ClassicUO.Renderer.Animations.Animations(f.Animations, device));
            }
        }

        private void Run(string name, Action load)
        {
            long start = Stopwatch.GetTimestamp();
            string error = null;

            try
            {
                load();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                StudioLog.Error($"{name}: {ex}");
            }

            _results.Add(new LoadResult(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error));
        }

        /// <summary>Per frame: atlas uploads of what the decode workers finished.</summary>
        public void Update(double budgetMs)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            Arts?.AdvanceFrame();
            Arts?.DrainReady(deadline);
            Gumps?.DrainReady(deadline);
            Texmaps?.DrainReady(deadline);
        }

        public void Dispose()
        {
            Arts?.CancelPending();
            Gumps?.CancelPending();
            Texmaps?.CancelPending();
            Animations?.CancelPending();
            Files.Dispose();
        }
    }
}
