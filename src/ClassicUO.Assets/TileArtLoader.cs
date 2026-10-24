// SPDX-License-Identifier: BSD-2-Clause

using System;
using Uo.Pack;

namespace ClassicUO.Assets;

/// <summary>The paperdoll appearances from tileart.uop, as the pack's tileart.bin lists them.</summary>
public sealed class TileArtLoader : UOFileLoader
{
    private TileArtAppearance[] _appearances = Array.Empty<TileArtAppearance>();

    public TileArtLoader(UOFileManager fileManager) : base(fileManager)
    {
    }

    public int Count => _appearances.Length;

    public override void Load()
    {
        _appearances = TileArtFile.Read(FileManager.Pack.PathOf(TableConst.TileArtFile));
    }

    public bool TryGetAppearance(ushort graphic, ushort mobGraphic, out uint appearanceId)
        => TileArtFile.TryGetAppearance(_appearances, graphic, mobGraphic, out appearanceId);
}
