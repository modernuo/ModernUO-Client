#region license

// Copyright (c) 2024, andreakarasho
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClassicUO.Assets
{
    public sealed class OneMapLoader : IDisposable
    {
        private readonly int idx;
        private readonly OneMapLoader inherit;

        private readonly FileInfo mapFileInfo;
        public long MapFileLength => mapFileInfo.Length;

        private DataReader _fileMap;
        private DataReader _fileStatics;
        private DataReader _fileIdxStatics;

        private readonly bool isuop;

        private DataReader _mapDif;
        private DataReader _staDif;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int BlocksCount => Width * Height;

        public IndexMap[] BlockData { get; private set; }

        public bool Exists => mapFileInfo.Exists;

        public bool Valid
        {
            get
            {
                if (!Exists)
                    return false;

                EnsureOpened();
                return _fileStatics != null && _fileStatics.StartAddress != IntPtr.Zero && _fileIdxStatics != null && _fileIdxStatics.StartAddress != IntPtr.Zero;
            }
        }

        private bool isOpened = false, isLoaded = false;

        private int MapPatchesCountPostponed = 0, StaticPatchesCountPostponed = 0;

        public OneMapLoader(int i, OneMapLoader _inherit)
        {
            idx = i;
            inherit = _inherit;

            string path = UOFileManager.GetUOFilePath($"map{i}LegacyMUL.uop");

            if (UOFileManager.IsUOPInstallation && File.Exists(path))
            {
                isuop = true;
                mapFileInfo = new FileInfo(path);
            }
            else
            {
                path = UOFileManager.GetUOFilePath($"map{i}.mul");
                mapFileInfo = new FileInfo(path);
            }
        }

        public void Dispose()
        {
            _fileMap?.Dispose();
            _fileStatics?.Dispose();
            _fileIdxStatics?.Dispose();
            _mapDif?.Dispose();
            _staDif?.Dispose();
        }

        public void SetSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        private void Open(int i)
        {
            string path = mapFileInfo.ToString();

            if (isuop)
            {
                _fileMap = new UOFileUop(path, $"build/map{i}legacymul/{{0:D8}}.dat");
            }
            else
            {
                if (mapFileInfo.Exists)
                {
                    _fileMap = new UOFile(path);
                }

                path = UOFileManager.GetUOFilePath($"mapdif{i}.mul");
                if (File.Exists(path))
                {
                    _mapDif = new UOFile(path);
                }

                path = UOFileManager.GetUOFilePath($"stadif{i}.mul");
                if (File.Exists(path))
                {
                    _staDif = new UOFile(path);
                }
            }

            path = UOFileManager.GetUOFilePath($"statics{i}.mul");

            if (File.Exists(path))
            {
                _fileStatics = new UOFile(path);
            }

            path = UOFileManager.GetUOFilePath($"staidx{i}.mul");

            if (File.Exists(path))
            {
                _fileIdxStatics = new UOFile(path);
            }
        }

        private void EnsureOpened()
        {
            if (isOpened)
            {
                return;
            }

            isOpened = true;
            Open(idx);
        }

        public unsafe void Load()
        {
            if (BlockData != null || !Exists)
            {
                return;
            }

            int mapblocksize = sizeof(MapBlock);
            int staticidxblocksize = sizeof(StaidxBlock);
            int staticblocksize = sizeof(StaticsBlock);
            int maxblockcount = Width * Height;
            BlockData = new IndexMap[maxblockcount];
            DataReader file = _fileMap;
            DataReader fileidx = _fileIdxStatics;
            DataReader staticfile = _fileStatics;

            if (inherit != null)
            {
                if (fileidx == null)
                {
                    inherit.EnsureOpened();
                    fileidx = inherit._fileIdxStatics;
                }

                if (staticfile == null)
                {
                    inherit.EnsureOpened();
                    staticfile = inherit._fileStatics;
                }
            }

            IntPtr staticidxaddress = fileidx.StartAddress;
            IntPtr endstaticidxaddress = staticidxaddress + (IntPtr) fileidx.Length;
            IntPtr staticaddress = staticfile.StartAddress;
            IntPtr endstaticaddress = staticaddress + (IntPtr) staticfile.Length;
            IntPtr mapddress = file.StartAddress;
            IntPtr endmapaddress = mapddress + (IntPtr) file.Length;
            IntPtr uopoffset = 0;
            int fileNumber = -1;

            for (int block = 0; block < maxblockcount; block++)
            {
                IntPtr realmapaddress = 0, realstaticaddress = 0;
                uint realstaticcount = 0;
                int blocknum = block;

                if (isuop)
                {
                    blocknum &= 4095;
                    int shifted = block >> 12;

                    if (fileNumber != shifted)
                    {
                        fileNumber = shifted;
                        var uop = file as UOFileUop;

                        if (shifted < uop.TotalEntriesCount)
                        {
                            var hash = UOFileUop.CreateHash(string.Format(uop.Pattern, shifted));

                            if (uop.TryGetUOPData(hash, out var dataIndex))
                            {
                                uopoffset = (IntPtr)dataIndex.Offset;
                            }
                        }
                    }
                }

                IntPtr address = mapddress + uopoffset + (IntPtr) (blocknum * mapblocksize);

                if (address < endmapaddress)
                {
                    realmapaddress = address;
                }

                IntPtr stidxaddress = staticidxaddress + (IntPtr) (block * staticidxblocksize);
                StaidxBlock* bb = (StaidxBlock*) stidxaddress;

                if (stidxaddress < endstaticidxaddress && bb->Size > 0 && bb->Position != 0xFFFFFFFF)
                {
                    IntPtr address1 = staticaddress + (IntPtr)bb->Position;

                    if (address1 < endstaticaddress)
                    {
                        realstaticaddress = address1;
                        realstaticcount = (uint) (bb->Size / staticblocksize);

                        if (realstaticcount > 1024)
                        {
                            realstaticcount = 1024;
                        }
                    }
                }

                ref IndexMap data = ref BlockData[block];
                data.MapAddress = realmapaddress;
                data.StaticAddress = realstaticaddress;
                data.StaticCount = realstaticcount;
                data.OriginalMapAddress = realmapaddress;
                data.OriginalStaticAddress = realstaticaddress;
                data.OriginalStaticCount = realstaticcount;
            }

            if (isuop)
            {
                ((UOFileUop)file)?.ClearHashes();
            }
        }

        public void EnsureLoaded()
        {
            if (isLoaded)
            {
                return;
            }

            EnsureOpened();

            isLoaded = true;
            Load();

            if (MapPatchesCountPostponed >= 0 && StaticPatchesCountPostponed >= 0)
            {
                /* ApplyPatches() was called before the map was loaded */
                DoApplyPatches(MapPatchesCountPostponed, StaticPatchesCountPostponed);
                MapPatchesCountPostponed = 0;
                StaticPatchesCountPostponed = 0;
            }
        }

        public unsafe bool DoApplyPatches(int mapPatchesCount, int staticPatchesCount)
        {
            ResetPatchesInBlockTable();

            if (!Exists)
            {
                return false;
            }

            EnsureLoaded();

            bool result = false;

            int maxBlockCount = BlocksCount;

            if (mapPatchesCount != 0 && _mapDif != null && _mapDif.HasData)
            {
                DataReader dif = _mapDif;
                using DataReader difl = new UOFile(UOFileManager.GetUOFilePath($"mapdifl{idx}.mul"));
                if (!difl.HasData)
                {
                    return false;
                }

                mapPatchesCount = Math.Min(mapPatchesCount, (int) (dif.Length / sizeof(MapBlock)));
                mapPatchesCount = Math.Min(mapPatchesCount, (int) difl.Length >> 2);

                var mapBlockPtr = (MapBlock*)dif.StartAddress;
                var diflPtr = (uint*)difl.StartAddress;

                for (int j = 0; j < mapPatchesCount; j++, mapBlockPtr++)
                {
                    uint blockIndex = *diflPtr++;

                    if (blockIndex < maxBlockCount)
                    {
                        BlockData[blockIndex].MapAddress = (IntPtr)mapBlockPtr;

                        result = true;
                    }
                }
            }

            if (staticPatchesCount != 0 && _staDif != null && _staDif.HasData)
            {
                using DataReader difl = new UOFile(UOFileManager.GetUOFilePath($"stadifl{idx}.mul"));
                using DataReader difi = new UOFile(UOFileManager.GetUOFilePath($"stadifi{idx}.mul"));
                if (!difl.HasData || !difi.HasData)
                {
                    return false;
                }

                IntPtr startAddress = _staDif.StartAddress;

                staticPatchesCount = Math.Min(staticPatchesCount, (int)(difi.Length / sizeof(StaidxBlock)));
                staticPatchesCount = Math.Min(staticPatchesCount, (int) difl.Length >> 2);

                int sizeOfStaicsBlock = sizeof(StaticsBlock);

                var sidx = (StaidxBlock*)difi.StartAddress;
                var diflPtr = (uint*)difl.StartAddress;

                for (int j = 0; j < staticPatchesCount; j++, sidx++)
                {
                    uint blockIndex = *diflPtr++;

                    if (blockIndex < maxBlockCount)
                    {
                        IntPtr realStaticAddress = 0;
                        int realStaticCount = 0;

                        if (sidx->Size > 0 && sidx->Position != 0xFFFF_FFFF)
                        {
                            realStaticAddress = startAddress + (IntPtr)sidx->Position;
                            realStaticCount = (int) (sidx->Size / sizeOfStaicsBlock);

                            if (realStaticCount > 0)
                            {
                                if (realStaticCount > 1024)
                                {
                                    realStaticCount = 1024;
                                }
                            }
                        }

                        BlockData[blockIndex].StaticAddress = realStaticAddress;

                        BlockData[blockIndex].StaticCount = (uint) realStaticCount;

                        result = true;
                    }
                }
            }

            return result;
        }

        public bool ApplyPatches(int mapPatchesCount, int staticPatchesCount)
        {
            if (!isLoaded)
            {
                /* the map is not yet loaded; postpone the operation
                   until the map is really loaded */
                MapPatchesCountPostponed = mapPatchesCount;
                StaticPatchesCountPostponed = staticPatchesCount;
                return false;
            }

            return DoApplyPatches(mapPatchesCount, staticPatchesCount);
        }

        private void ResetPatchesInBlockTable()
        {
            if (BlockData == null)
            {
                return;
            }

            int maxBlockCount = BlocksCount;

            if (maxBlockCount < 1)
            {
                return;
            }

            if (!isuop && _fileMap != null && _fileMap.HasData &&
                _fileIdxStatics != null && _fileIdxStatics.HasData &&
                _fileStatics != null && _fileStatics.HasData)
            {
                for (int block = 0; block < maxBlockCount; block++)
                {
                    ref IndexMap index = ref BlockData[block];
                    index.MapAddress = index.OriginalMapAddress;
                    index.StaticAddress = index.OriginalStaticAddress;
                    index.StaticCount = index.OriginalStaticCount;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref IndexMap GetIndex(int x, int y)
        {
            int block = x * Height + y;

            return ref BlockData[block];
        }
    }

    public sealed class MapLoader : IDisposable
    {
        private static MapLoader _instance;

        // cannot be a const, due to UOLive implementation
        public static int MAPS_COUNT = 6;

        private MapLoader()
        {
        }

        public void Dispose()
        {
            foreach (var m in Maps)
                m?.Dispose();
        }

        public static MapLoader Instance
        {
            get => _instance ?? (_instance = new MapLoader());
            set
            {
                _instance?.Dispose();
                _instance = value;
            }
        }

        public static string MapsLayouts { get; set; }

        // ReSharper disable RedundantExplicitArraySize
        public int[,] MapsDefaultSize { get; private set; } = new int[6, 2]
            // ReSharper restore RedundantExplicitArraySize
            {
                {
                    7168, 4096
                },
                {
                    7168, 4096
                },
                {
                    2304, 1600
                },
                {
                    2560, 2048
                },
                {
                    1448, 1448
                },
                {
                    1280, 4096
                }
            };

        public OneMapLoader[] Maps;

        private void Initialize()
        {
            Maps = new OneMapLoader[MAPS_COUNT];
        }

        public Task Load()
        {
            return Task.Run
            (
                () =>
                {
                    bool foundOneMap = false;

                    if (!string.IsNullOrEmpty(MapsLayouts))
                    {
                        string[] values = MapsLayouts.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                        MAPS_COUNT = values.Length;
                        MapsDefaultSize = new int[values.Length, 2];

                        Log.Trace($"default maps size overraided. [count: {MAPS_COUNT}]");


                        int index = 0;

                        char[] splitchar = new char[1] { ',' };

                        foreach (string s in values)
                        {
                            string[] v = s.Split(splitchar, StringSplitOptions.RemoveEmptyEntries);

                            if (v.Length >= 2 && int.TryParse(v[0], out int width) && int.TryParse(v[1], out int height))
                            {
                                MapsDefaultSize[index, 0] = width;
                                MapsDefaultSize[index, 1] = height;

                                Log.Trace($"overraided map size: {width},{height}  [index: {index}]");
                            }
                            else
                            {
                                Log.Error($"Error parsing 'width,height' values: '{s}'");
                            }

                            ++index;
                        }
                    }


                    Initialize();

                    for (var i = 0; i < MAPS_COUNT; ++i)
                    {
                        Maps[i] = new OneMapLoader(i, i == 1 ? Maps[0] : null);

                        if (Maps[i].Exists)
                            foundOneMap = true;
                    }

                    if (!foundOneMap)
                    {
                        throw new FileNotFoundException("No maps found.");
                    }

                    for (var i = 0; i < MAPS_COUNT; ++i)
                    {
                        Maps[i].SetSize(MapsDefaultSize[i, 0] >> 3,
                                        MapsDefaultSize[i, 1] >> 3);
                    }

                    // This is an hack to patch correctly all maps when you have to fake map1
                    if (!Maps[1].Exists)
                    {
                        Maps[1] = Maps[0];
                    }
                }
            );
        }

        public bool ApplyPatches(ref StackDataReader reader)
        {
            int patchesCount = (int) reader.ReadUInt32BE();

            if (patchesCount < 0)
            {
                patchesCount = 0;
            }

            if (patchesCount > MAPS_COUNT)
            {
                patchesCount = MAPS_COUNT;
            }

            bool result = false;

            for (int i = 0; i < patchesCount; i++)
            {
                //SanitizeMapIndex(ref idx);

                int mapPatchesCount = (int) reader.ReadUInt32BE();
                int staticPatchesCount = (int) reader.ReadUInt32BE();

                if (Maps[i].ApplyPatches(mapPatchesCount, staticPatchesCount))
                {
                    result = true;
                }
            }

            return result;
        }

        public void SanitizeMapIndex(ref int map)
        {
            if (map == 1 && !Maps[1].Valid)
            {
                map = 0;
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref IndexMap GetIndex(int map, int x, int y)
        {
            return ref Maps[map].GetIndex(x, y);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct StaticsBlock
    {
        public readonly ushort Color;
        public readonly byte X;
        public readonly byte Y;
        public readonly sbyte Z;
        public readonly ushort Hue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly ref struct StaidxBlock
    {
        public readonly uint Position;
        public readonly uint Size;
        public readonly uint Unknown;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly ref struct MapCells
    {
        public readonly ushort TileID;
        public readonly sbyte Z;
    }

    //[StructLayout(LayoutKind.Sequential, Pack = 1)]
    //public struct MapBlock
    //{
    //    public readonly uint Header;
    //    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    //    public MapCells[] Cells;
    //}

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4 + 64 * 3)]
    public readonly ref struct MapBlock
    {
        public readonly uint Header;
        public readonly unsafe MapCells* Cells;
    }

    //[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4 + 64 * 3)]
    //public struct MapBlock2
    //{
    //    public readonly uint Header;
    //    public IntPtr Cells;
    //}

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct RadarMapcells
    {
        public readonly ushort Graphic;
        public readonly sbyte Z;
        public readonly bool IsLand;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct RadarMapBlock
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public readonly RadarMapcells[,] Cells;
    }

    public struct IndexMap
    {
        public IntPtr MapAddress;
        public IntPtr OriginalMapAddress;
        public IntPtr OriginalStaticAddress;
        public uint OriginalStaticCount;
        public IntPtr StaticAddress;
        public uint StaticCount;
        public static IndexMap Invalid = new IndexMap();

        public bool HasMapCells => MapAddress != 0;
        public bool HasStaticsBlocks => StaticAddress != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ref MapCells GetMapCell(int i)
        {
            MapBlock* mp = (MapBlock*) MapAddress;
            MapCells* cells = (MapCells*) &mp->Cells;
            return ref cells[i];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref MapCells GetMapCell(int x, int y)
        {
            return ref GetMapCell((y << 3) + x);
        }

        public ReadOnlySpan<StaticsBlock> StaticsBlocks {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                unsafe {
                    return new ReadOnlySpan<StaticsBlock>((void *)StaticAddress, (int)StaticCount);
                }
            }
        }
    }
}
