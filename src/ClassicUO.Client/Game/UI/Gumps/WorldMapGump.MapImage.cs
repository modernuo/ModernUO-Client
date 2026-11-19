// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.IO;
using ClassicUO.Network.Encryption;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static ClassicUO.Game.UI.Gumps.WorldMapGump;
using SpriteFont = ClassicUO.Renderer.SpriteFont;

namespace ClassicUO.Game.UI.Gumps
{
    internal partial class WorldMapGump
    {
        /// <summary>
        /// Hands the image a load task decoded to the game thread, which owns the texture (GPU resources are created and
        /// disposed only there). Loads are numbered on the game thread by <see cref="Request"/>; only the latest request's
        /// image is kept. An older one, one posted after <see cref="Close"/>, and one replaced before it was taken are
        /// disposed, so a superseded load or one that ends after the gump closed leaks nothing.
        /// </summary>
        internal sealed class MapImageHandover<T> where T : class, IDisposable
        {
            private readonly object _lock = new object();
            private T _pending;
            private int _latest;
            private bool _closed;

            /// <summary>Starts a new load (game thread); drops the image of an older one that was not taken yet.</summary>
            public int Request()
            {
                T dropped;
                int load;

                lock (_lock)
                {
                    dropped = _pending;
                    _pending = null;
                    load = ++_latest;
                }

                dropped?.Dispose();

                return load;
            }

            /// <summary>Whether a load is still wanted: lets the task skip decoding a superseded or abandoned image.</summary>
            public bool IsCurrent(int load)
            {
                lock (_lock)
                {
                    return !_closed && load == _latest;
                }
            }

            /// <summary>Posts a load's image (any thread). Returns false when it was dropped (and disposed).</summary>
            public bool Post(int load, T image)
            {
                T dropped;
                bool kept;

                lock (_lock)
                {
                    kept = !_closed && load == _latest;
                    dropped = kept ? _pending : image;

                    if (kept)
                    {
                        Volatile.Write(ref _postedAt, System.Diagnostics.Stopwatch.GetTimestamp());
                        _pending = image;
                    }
                }

                dropped?.Dispose();

                return kept;
            }

            /// <summary>The posted image, or null (game thread). The caller owns and disposes it.</summary>
            public T Take()
            {
                if (Volatile.Read(ref _pending) == null)
                {
                    return null;
                }

                lock (_lock)
                {
                    T image = _pending;
                    _pending = null;

                    return image;
                }
            }

            /// <summary>An image is posted and not taken yet (any thread; a volatile read).</summary>
            public bool HasPending => Volatile.Read(ref _pending) != null;

            /// <summary>Stopwatch timestamp of the last kept post (CUO_WORLDMAP_IDLE_CHECK measures how long it waited).</summary>
            public long PostedAt => Volatile.Read(ref _postedAt);

            private long _postedAt;

            /// <summary>The gump closed: drops a pending image and every later post.</summary>
            public void Close()
            {
                T dropped;

                lock (_lock)
                {
                    _closed = true;
                    dropped = _pending;
                    _pending = null;
                }

                dropped?.Dispose();
            }
        }

        /// <summary>The cached world map image of a facet. Gargoyle-only statics are drawn for a gargoyle player, so the
        /// image depends on the race too; the non-gargoyle name is the one used before the race was part of it.</summary>
        internal static string MapCacheFileName(int mapIndex, string facetHash, bool playerIsGargoyle) =>
            playerIsGargoyle ? $"map{mapIndex}_{facetHash}_gargoyle.png" : $"map{mapIndex}_{facetHash}.png";

        private unsafe void LoadMap(int mapIndex, bool gargoyle, int load)
        {
            if (mapIndex < 0 || mapIndex >= MapLoader.MAPS_COUNT)
            {
                return;
            }

            // superseded by a later ChangeMap, or the gump closed: don't build or decode an image nobody will show
            if (!World.InGame || !_mapImages.IsCurrent(load))
            {
                return;
            }

            try
            {
                const int OFFSET_PIX = 2;
                const int OFFSET_PIX_HALF = OFFSET_PIX / 2;

                int realWidth = Client.Game.UO.FileManager.Maps.MapsDefaultSize[mapIndex, 0];
                int realHeight = Client.Game.UO.FileManager.Maps.MapsDefaultSize[mapIndex, 1];

                int fixedWidth = Client.Game.UO.FileManager.Maps.MapBlocksSize[mapIndex, 0];
                int fixedHeight = Client.Game.UO.FileManager.Maps.MapBlocksSize[mapIndex, 1];

                // one load at a time across gumps: a map closed mid-build and opened again starts a second gump whose
                // task would build and write the same cache file (and both touch the static _mapCache)
                _mapBuildGate.Wait();
                using var buildGate = new GateRelease(_mapBuildGate);

                if (!_mapImages.IsCurrent(load))
                {
                    return;
                }

                var mapKey = gargoyle ? $"{mapIndex}_gargoyle" : $"{mapIndex}";

                if (!_mapCache.TryGetValue(mapKey, out var fileMapPath))
                {
                    var hash = Client.Game.UO.FileManager.Maps.GetFacetHash(mapIndex);
                    fileMapPath = Path.Combine(_mapsCachePath, MapCacheFileName(mapIndex, hash, gargoyle));
                    _mapCache[mapKey] = fileMapPath;
                }

                // P-7: a map built here is posted from its build buffer, not read back from the PNG it was written to
                WorldMapImage built = null;

                if (!File.Exists(fileMapPath))
                {
                    // RGBA, one uint per pixel (R in the low byte); native (up to ~118 MB for Felucca); the image owns it
                    // once written, and the finally frees it on any earlier failure
                    uint* imgPixels = null;

                    try
                    {
                        Interlocked.Increment(ref _mapLoading);

                        var size = (realWidth + OFFSET_PIX) * (realHeight + OFFSET_PIX);
                        var allZ = new sbyte[size];

                        imgPixels = (uint*)NativeMemory.AllocZeroed((nuint)size, sizeof(uint));
                        var imgSpan = new Span<uint>(imgPixels, size);

                        var huesLoader = Client.Game.UO.FileManager.Hues;

                        int bx, by, mapX = 0, mapY = 0, x, y;

                        var maps = Client.Game.UO.FileManager.Maps;
                        int regionBlocks = maps.RegionBlocks(mapIndex);

                        for (int rx = 0; rx * regionBlocks < fixedWidth; ++rx)
                        {
                            for (int ry = 0; ry * regionBlocks < fixedHeight; ++ry)
                            {
                                var region = maps.ReadRegion(mapIndex, rx, ry);

                                if (region == null)
                                {
                                    continue;
                                }

                                for (int lx = 0; lx < region.BlocksW; ++lx)
                                {
                                    bx = rx * regionBlocks + lx;
                                    mapX = bx << 3;

                                    for (int ly = 0; ly < region.BlocksH; ++ly)
                                    {
                                        by = ry * regionBlocks + ly;

                                        if (!region.TryGetBlock(lx, ly, out var mapBlock))
                                        {
                                            continue;
                                        }

                                        var cells = mapBlock.Land;
                                        mapY = by << 3;

                                        for (y = 0; y < 8; ++y)
                                        {
                                            var block = (mapY + y + OFFSET_PIX_HALF) * (realWidth + OFFSET_PIX) + mapX + OFFSET_PIX_HALF;
                                            var pos = y << 3;

                                            for (x = 0; x < 8; ++x, ++pos, ++block)
                                            {
                                                ushort color = (ushort)(0x8000 | huesLoader.GetRadarColorData(cells[pos].TileId));

                                                imgSpan[block] = HuesHelper.Color16To32(color) | 0xFF_00_00_00;
                                                allZ[block] = cells[pos].Z;
                                            }
                                        }

                                        foreach (ref readonly var sb in mapBlock.Statics)
                                        {
                                            if (GameObject.CanBeDrawn(sb.Graphic, gargoyle))
                                            {
                                                int block = (mapY + sb.Y + OFFSET_PIX_HALF) * (realWidth + OFFSET_PIX) + mapX + sb.X + OFFSET_PIX_HALF;

                                                if (sb.Z >= allZ[block])
                                                {
                                                    var color = (ushort)(0x8000 | (sb.Hue != 0 ? huesLoader.GetColor16(16384, sb.Hue) : huesLoader.GetRadarColorData(sb.Graphic + 0x4000)));

                                                    imgSpan[block] = HuesHelper.Color16To32(color) | 0xFF_00_00_00;
                                                    allZ[block] = sb.Z;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        int real_width_less_one = realWidth - 1;
                        int real_height_less_one = realHeight - 1;
                        const float MAG_0 = 80f / 100f;
                        const float MAG_1 = 100f / 80f;

                        for (mapY = 1; mapY < real_height_less_one; ++mapY)
                        {
                            int blockCurrent = (mapY + OFFSET_PIX_HALF) * (realWidth + OFFSET_PIX) + OFFSET_PIX_HALF;
                            int blockNext = (mapY + 1 + OFFSET_PIX_HALF) * (realWidth + OFFSET_PIX) + OFFSET_PIX_HALF;

                            for (mapX = 1; mapX < real_width_less_one; ++mapX)
                            {
                                sbyte z0 = allZ[++blockCurrent];
                                sbyte z1 = allZ[blockNext++];

                                if (z0 == z1)
                                {
                                    continue;
                                }

                                ref uint cc = ref imgSpan[blockCurrent];
                                if (cc == 0)
                                {
                                    continue;
                                }

                                byte r = (byte)(cc & 0xFF);
                                byte g = (byte)((cc >> 8) & 0xFF);
                                byte b = (byte)((cc >> 16) & 0xFF);
                                byte a = (byte)((cc >> 24) & 0xFF);

                                if (r != 0 || g != 0 || b != 0)
                                {
                                    if (z0 < z1)
                                    {
                                        r = (byte)Math.Min(0xFF, r * MAG_0);
                                        g = (byte)Math.Min(0xFF, g * MAG_0);
                                        b = (byte)Math.Min(0xFF, b * MAG_0);
                                    }
                                    else
                                    {
                                        r = (byte)Math.Min(0xFF, r * MAG_1);
                                        g = (byte)Math.Min(0xFF, g * MAG_1);
                                        b = (byte)Math.Min(0xFF, b * MAG_1);
                                    }

                                    cc = (uint)(r | (g << 8) | (b << 16) | (a << 24));
                                }
                            }
                        }


                        //var quantizer = new OctreeQuantizer();
                        //for (var i = 0; i < buffer.Length; i++)
                        //{
                        //    quantizer.AddColor(buffer[i]);
                        //}

                        //var palette = quantizer.GetPalette(256);

                        //for (var i = 0; i < buffer.Length; i++)
                        //{
                        //    var paletteIndex = quantizer.GetPaletteIndex(buffer[i]);
                        //    buffer[i] = palette[paletteIndex];
                        //}

                        //quantizer.Clear();

                        Directory.CreateDirectory(_mapsCachePath);
                        using var stream2 = AtomicFile.OpenWrite(fileMapPath, 3000);
                        Uo.Render.Png.Write(stream2, realWidth + OFFSET_PIX, realHeight + OFFSET_PIX, new ReadOnlySpan<byte>(imgPixels, size * sizeof(uint)));
                        stream2.Commit();

                        // the same pixels a decode of the PNG returns (lossless RGBA8); ownership moves in one statement
                        built = WorldMapImage.AdoptNativeMemory(realWidth + OFFSET_PIX, realHeight + OFFSET_PIX, imgPixels);
                        imgPixels = null;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"error loading worldmap: {ex}");

                        return;
                    }
                    finally
                    {
                        NativeMemory.Free(imgPixels);
                        Interlocked.Decrement(ref _mapLoading);
                    }
                }

                WorldMapImage image = built;

                try
                {
                    if (!_mapImages.IsCurrent(load))
                    {
                        return; // the finally disposes a built image
                    }

                    // decoded here, uploaded by Update: FNA3D/SDL_GPU textures are created and disposed on the game thread
                    if (image == null && File.Exists(fileMapPath))
                    {
                        image = DecodeMapImage(fileMapPath);
                    }

                    if (image != null)
                    {
                        // logged before Post: once posted, Update may upload it before this task logs again
                        Log.Info($"[worldmap] map {mapIndex} decoded {image.Width}x{image.Height}, load {load}");

                        WorldMapImage posted = image;
                        image = null;

                        if (!_mapImages.Post(load, posted))
                        {
                            Log.Info($"[worldmap] load {load} dropped (superseded or the gump closed)");
                        }
                    }
                }
                finally
                {
                    // not posted (superseded, or an exception before Post): nothing else owns it
                    image?.Dispose();
                }
            }
            catch (ThreadInterruptedException)
            {
                _mapLoading = 0;
            }
            catch (Exception ex)
            {
                // a faulted task is never observed: log it, the next ChangeMap's load still runs (ContinueWith)
                Log.Error($"error loading worldmap: {ex}");
            }
        }

        private static readonly SemaphoreSlim _mapBuildGate = new SemaphoreSlim(1, 1);

        private readonly struct GateRelease : IDisposable
        {
            private readonly SemaphoreSlim _gate;

            public GateRelease(SemaphoreSlim gate) => _gate = gate;

            public void Dispose() => _gate.Release();
        }

        /// <summary>Decodes a cached world map PNG to RGBA (any thread; no GPU work). Null when it can't be read.</summary>
        internal static WorldMapImage DecodeMapImage(string path)
        {
            try
            {
                using var stream = AtomicFile.OpenRead(path);

                return WorldMapImage.DecodePng(stream);
            }
            catch (Exception ex)
            {
                Log.Error($"error decoding worldmap {path}: {ex}");

                return null;
            }
        }

        /// <summary>Creates the map texture (game thread); called by <see cref="WorldMapTextureOwner{TTexture}.Adopt"/>,
        /// which stores it before anything else runs.</summary>
        private static Texture2D AllocateMapTexture(WorldMapImage image) =>
            new Texture2D(Client.Game.GraphicsDevice, image.Width, image.Height);

        /// <summary>Uploads the pixels into the owned texture (game thread); the owner disposes the texture if this throws.</summary>
        private static void FillMapTexture(Texture2D texture, WorldMapImage image)
        {
            texture.SetDataPointerEXT(0, null, image.Pixels, image.ByteLength);

            ClassicUO.Renderer.TextureUploads.Changed();
        }

        private static readonly Func<WorldMapImage, Texture2D> _allocateMapTexture = AllocateMapTexture;
        private static readonly Action<Texture2D, WorldMapImage> _fillMapTexture = FillMapTexture;
    }
}
