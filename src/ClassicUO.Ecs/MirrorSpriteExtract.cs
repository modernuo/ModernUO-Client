// SPDX-License-Identifier: BSD-2-Clause

using System;
using Friflo.Engine.ECS;

namespace ClassicUO.Ecs
{
    public enum MirrorSpriteKind : byte
    {
        Item,
        Mobile,
        Player
    }

    public struct MirrorSprite
    {
        public uint Serial;
        public ushort Graphic, Hue, X, Y;
        public sbyte Z;
        public byte Facing, Flags;
        public MirrorSpriteKind Kind;
    }

    // Read-only system: on-ground items and mobiles within range of a tile, as flat sprite records for the GPU renderer.
    // Zero allocation after warm-up.
    public sealed class MirrorSpriteExtract
    {
        private readonly ArchetypeQuery _items, _mobiles;
        private MirrorSprite[] _buffer = new MirrorSprite[1024];
        private int _count;

        public MirrorSpriteExtract(EntityStore store)
        {
            ComponentTypes types = ComponentTypes.Get<Serial, Graphic, Hue, Position, Facing>();
            types.Add(ComponentTypes.Get<Flags>());

            _items = store.Query().AllComponents(types).AllTags(Tags.Get<ItemTag, OnGround>());
            _mobiles = store.Query().AllComponents(types).AllTags(Tags.Get<MobileTag>());
        }

        public ReadOnlySpan<MirrorSprite> Sprites => new ReadOnlySpan<MirrorSprite>(_buffer, 0, _count);

        public int ItemCount { get; private set; }

        public int MobileCount { get; private set; }

        public ReadOnlySpan<MirrorSprite> Run(int centerX, int centerY, int range)
        {
            _count = 0;
            ItemCount = Collect(_items, centerX, centerY, range, false);
            MobileCount = Collect(_mobiles, centerX, centerY, range, true);

            return Sprites;
        }

        private int Collect(ArchetypeQuery query, int cx, int cy, int range, bool mobiles)
        {
            int start = _count;

            foreach (Archetype arch in query.Archetypes)
            {
                int n = arch.Count;

                if (n == 0)
                {
                    continue;
                }

                ReadOnlySpan<Serial> serials = arch.Components<Serial>();
                ReadOnlySpan<Graphic> graphics = arch.Components<Graphic>();
                ReadOnlySpan<Hue> hues = arch.Components<Hue>();
                ReadOnlySpan<Position> positions = arch.Components<Position>();
                ReadOnlySpan<Facing> facings = arch.Components<Facing>();
                ReadOnlySpan<Flags> flags = arch.Components<Flags>();
                bool isPlayer = mobiles && arch.Tags.Has<PlayerTag>();
                MirrorSpriteKind kind = isPlayer ? MirrorSpriteKind.Player : mobiles ? MirrorSpriteKind.Mobile : MirrorSpriteKind.Item;

                if (_count + n > _buffer.Length)
                {
                    Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + n));
                }

                for (int i = 0; i < n; i++)
                {
                    ref readonly Position p = ref positions[i];

                    if (Math.Abs(p.X - cx) > range || Math.Abs(p.Y - cy) > range)
                    {
                        continue;
                    }

                    ref MirrorSprite s = ref _buffer[_count++];
                    s.Serial = serials[i].Value;
                    s.Graphic = graphics[i].Value;
                    s.Hue = hues[i].Value;
                    s.X = p.X;
                    s.Y = p.Y;
                    s.Z = p.Z;
                    s.Facing = facings[i].Value;
                    s.Flags = flags[i].Value;
                    s.Kind = kind;
                }
            }

            return _count - start;
        }
    }
}
