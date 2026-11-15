// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class MultiLoader : UOFileLoader
    {
        public const int MAX_MULTI_DATA_INDEX_COUNT = 0x2200;

        private MultiEntry[] _multis = Array.Empty<MultiEntry>();
        private MultiComponent[] _components = Array.Empty<MultiComponent>();

        public MultiLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public int Count => _multis.Length;

        public override void Load()
        {
            (_multis, _components) = MultiFile.Read(FileManager.Pack.PathOf(TableConst.MultisFile));
        }

        public List<MultiInfo> GetMultis(uint idx)
        {
            var components = MultiFile.Components(_multis, _components, (int)idx);
            var list = new List<MultiInfo>(components.Length);

            foreach (ref readonly var c in components)
            {
                list.Add(new MultiInfo
                {
                    ID = c.Graphic,
                    X = c.X,
                    Y = c.Y,
                    Z = c.Z,
                    IsVisible = (c.Flags & MultiComponentFlags.Visible) != 0
                });
            }

            return list;
        }
    }

    public struct MultiInfo
    {
        public ushort ID;
        public short X;
        public short Y;
        public short Z;
        public bool IsVisible;
    }
}
