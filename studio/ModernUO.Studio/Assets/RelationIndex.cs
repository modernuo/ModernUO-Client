namespace ModernUO.Studio.Assets
{
    /// <summary>"Used by" lookups for the inspectors' relations, each built on first use for the open pack.</summary>
    internal sealed class RelationIndex
    {
        private readonly StudioAssets _assets;
        private Dictionary<int, List<int>> _multisByStatic;
        private Dictionary<int, List<int>> _landByTexmap;

        public RelationIndex(StudioAssets assets)
        {
            _assets = assets;
        }

        private static readonly IReadOnlyList<int> _none = Array.Empty<int>();

        /// <summary>The multis that have the static among their components.</summary>
        public IReadOnlyList<int> MultisUsing(int staticId)
        {
            if (_multisByStatic == null)
            {
                _multisByStatic = new Dictionary<int, List<int>>();
                var multis = _assets.Files.Multis;

                for (int id = 0; id < multis.Count; id++)
                {
                    var list = multis.GetMultis((uint)id);

                    if (list == null)
                    {
                        continue;
                    }

                    foreach (var c in list)
                    {
                        if (!_multisByStatic.TryGetValue(c.ID, out var users))
                        {
                            _multisByStatic[c.ID] = users = new List<int>();
                        }

                        if (users.Count == 0 || users[^1] != id)
                        {
                            users.Add(id);
                        }
                    }
                }
            }

            return _multisByStatic.TryGetValue(staticId, out var found) ? found : _none;
        }

        /// <summary>The land tiles whose tiledata names the texmap.</summary>
        public IReadOnlyList<int> LandUsing(int texmap)
        {
            if (_landByTexmap == null)
            {
                _landByTexmap = new Dictionary<int, List<int>>();
                var land = _assets.Files.TileData.LandData;

                for (int id = 0; land != null && id < land.Length; id++)
                {
                    if (!_landByTexmap.TryGetValue(land[id].TexID, out var users))
                    {
                        _landByTexmap[land[id].TexID] = users = new List<int>();
                    }

                    users.Add(id);
                }
            }

            return _landByTexmap.TryGetValue(texmap, out var found) ? found : _none;
        }
    }
}
