// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.Map;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;
using Uo.Render;

namespace ClassicUO.Game
{
    /// <summary>
    /// W5: what one mobile's <see cref="MobileAnimationSystem"/> selection and
    /// <see cref="MobileSpriteExtract"/> emission produced, reused while their inputs stay the same. Most mobiles change
    /// animation frame every 80–100 ms, so at hundreds of frames per second nearly every frame repeats the last one.
    /// <list type="bullet">
    /// <item><b>Selection</b> is a function of the sampled state (<see cref="SelectKey"/>): flags, body, server action,
    /// facing, frame, the equipment version and, for the seat lookup, the tile (position, the chunk's content version,
    /// the season). Not reused while the mobile has steps queued (the walk test reads the step) or when the selection
    /// rolled a random action.</item>
    /// <item><b>Instances</b> additionally depend on alpha, override hue, body hue and the pack. They are stored relative
    /// to the mobile's draw origin with the recipe of their depth, so the camera, the walking offset, the base depth and
    /// the tile x are applied again each frame exactly as <see cref="MobileSpriteExtract"/> computes them.</item>
    /// </list>
    /// </summary>
    internal sealed class MobileFrameCache
    {
        public struct SelectKey : IEquatable<SelectKey>
        {
            public AnimStateFlags Flags;
            public uint NextFrameTime;
            public uint TileVersion;
            public uint EquipVersion;
            public ushort Body, Graphic, X, Y;
            public byte ServerGroup, Facing, Frame, Season;
            public sbyte Z;
            public Chunk Tile;

            public bool Equals(SelectKey o) =>
                Flags == o.Flags && NextFrameTime == o.NextFrameTime && TileVersion == o.TileVersion && Body == o.Body && Graphic == o.Graphic &&
                EquipVersion == o.EquipVersion && X == o.X && Y == o.Y && ServerGroup == o.ServerGroup && Facing == o.Facing && Frame == o.Frame &&
                Season == o.Season && Z == o.Z && ReferenceEquals(Tile, o.Tile);

            public override bool Equals(object obj) => obj is SelectKey k && Equals(k);

            public override int GetHashCode() => (int)(NextFrameTime ^ (uint)Flags ^ ((uint)X << 16 | Y));
        }

        // selection
        public bool SelectValid;
        public SelectKey Key;
        public AnimationState Selected;
        public uint SelectGen;

        // emission
        public bool EmitValid;
        public uint EmitSelectGen;
        public byte Alpha;
        public ushort OverrideHue, BodyHue;
        public PackSet Pack;
        // clip-2: recorded with depth_model on (the instances carry the composite anchor, SpriteInstance.GeoMobile)
        public bool Geo;
        public Entry[] Entries = new Entry[8];
        public int Count;

        /// <summary>An instance relative to the draw origin; depth = Kind == 0 ? baseDepth : (baseDepth + 1f) + Add, as
        /// MobileSpriteExtract writes it.</summary>
        public struct Entry
        {
            public SpriteInstance Instance;
            public float Add;
            public byte Kind;
        }

        /// <summary>E5: the last replay of <see cref="Entries"/> in absolute (buffer) coordinates, valid for the tag below
        /// (see MobileSpriteExtract.Replay). Reset whenever the entries are re-recorded.</summary>
        public SpriteInstance[] Abs = Array.Empty<SpriteInstance>();
        public bool AbsValid;
        public int AbsDx, AbsDy, AbsObjX;
        public float AbsDepth;

        public Rectangle FrameInfo;
        public MobileSpriteExtract.LightRequest[] Lights = new MobileSpriteExtract.LightRequest[2];
        public int LightCount;

        /// <summary>
        /// The Legacy sink (LegacySpriteSink): the Legacy batcher calls the last select resolved to, relative to the draw
        /// origin (position + walking offset), with the recipe of their depth, plus FrameInfo and the lights. Recorded only
        /// when the selection is cacheable, the mobile is not seated and every layer and record had its atlas texture (the
        /// atlas only gains textures, except that anim_atlas_lru frees pages and bumps Animations.AtlasEpoch, which this
        /// recording keeps in <see cref="LegacyAtlasEpoch"/>). Replayed while the selection
        /// (<see cref="SelectGen"/>), alpha, override hue, body hue and the sprite source are the same.
        /// </summary>
        public bool LegacyValid;
        public uint LegacySelectGen;
        // anim_atlas_lru: Animations.AtlasEpoch when recorded; a freed atlas page makes every recording stale
        public int LegacyAtlasEpoch;
        public byte LegacyAlpha;
        public ushort LegacyOverrideHue, LegacyBodyHue;
        public ILegacySpriteSource LegacySource;
        public LegacyEntry[] LegacyEntries = Array.Empty<LegacyEntry>();
        public int LegacyCount;
        public Rectangle LegacyFrameInfo;
        public MobileSpriteExtract.LightRequest[] LegacyLights = Array.Empty<MobileSpriteExtract.LightRequest>();
        public int LegacyLightCount;

        /// <summary>One Legacy batcher call (a shadow or strip draw: seated mobiles are not recorded), about 56 bytes; at
        /// replay its position is (origin + X, origin + Y) and its depth MobileSpriteExtract.DepthOf(baseDepth, Kind, Add).</summary>
        public struct LegacyEntry
        {
            public Texture2D Texture;
            public Rectangle Source;
            public Vector3 Hue;
            public int X, Y;
            public float Add;
            public byte Kind;
            public bool Flip;
            public SpriteCaptureMode Mode;
        }

        // sized to the record count (rounded up to 4): one array per drawn mobile, grown only when its layers grow
        public void EnsureLegacyCapacity(int n)
        {
            if (LegacyEntries.Length < n)
            {
                Array.Resize(ref LegacyEntries, (n + 3) & ~3);
            }
        }

        public void EnsureCapacity(int n)
        {
            if (Entries.Length < n)
            {
                Array.Resize(ref Entries, Math.Max(n, Entries.Length * 2));
            }
        }

        // A mobile leaving range returns its cache (Mobile.Destroy) and the next mobile coming into range takes it, with the
        // arrays it grew to, instead of a new cache and its arrays per mobile (~0.3-0.5 MB per 15 s of walking through a crowd).
        // At most MAX_POOLED caches are kept, so about 32 * 2 KB per client.
        private const int MAX_POOLED = 32;
        private static readonly System.Collections.Generic.Stack<MobileFrameCache> _pool = new System.Collections.Generic.Stack<MobileFrameCache>();

        /// <summary>Stats line ([objpools]): caches pooled now, rents that allocated, returns left out (pool full).</summary>
        public static int PoolCount => _pool.Count;
        public const int PoolMax = MAX_POOLED;
        public static long PoolMisses, PoolDropped;

        internal static MobileFrameCache Rent()
        {
            MobileFrameCache c = null;

            lock (_pool)
            {
                if (_pool.Count != 0)
                {
                    c = _pool.Pop();
                }
            }

            if (c == null)
            {
                c = new MobileFrameCache();
                PoolMisses++;
            }
#if CUO_DEVTOOLS
            c.InPool = false;
#endif
            return c;
        }

#if CUO_DEVTOOLS
        // CUO_DEVTOOLS guards for the pool: a cache returned while already pooled, and a cache read for a mobile other than
        // the one that rented it. Both are counted in the [mobile-cache-check] summary.
        public bool InPool;
        public uint OwnerSerial;
        public static long DoubleReturns, OwnerMismatches;

        internal void CheckOwner(uint serial)
        {
            if (OwnerSerial == 0)
            {
                OwnerSerial = serial;
            }
            else if ((OwnerSerial & 0x7FFFFFFF) != (serial & 0x7FFFFFFF))
            {
                // The death packet re-keys a dying mobile to serial | 0x80000000 (WorldWriter.RekeyDeadMobile): same
                // Mobile, same cache, not a mismatch.
                OwnerMismatches++;
                ClassicUO.Utility.Logging.Log.Warn($"[mobile-cache-check] frame cache of 0x{OwnerSerial:X8} used by 0x{serial:X8}");
                OwnerSerial = serial;
            }
        }
#endif

        internal static bool Return(MobileFrameCache cache)
        {
#if CUO_DEVTOOLS
            if (cache.InPool)
            {
                DoubleReturns++;
                ClassicUO.Utility.Logging.Log.Warn("[mobile-cache-check] frame cache returned while already in the pool");
                return false;
            }
#endif

            cache.Reset();

            lock (_pool)
            {
                if (_pool.Count < MAX_POOLED)
                {
#if CUO_DEVTOOLS
                    cache.InPool = true;
#endif
                    _pool.Push(cache);

                    return true;
                }
            }

            PoolDropped++;

            return false;
        }

        // What a rented cache must look like: nothing valid, nothing counted, no references to the previous mobile.
        internal void Reset()
        {
#if CUO_DEVTOOLS
            OwnerSerial = 0;
#endif
            Invalidate();
            Count = 0;
            LightCount = 0;
            LegacyCount = 0;
            LegacyLightCount = 0;
            Pack = null;
            LegacySource = null;
            Key = default;
            Selected = default;
            Array.Clear(LegacyEntries);
        }

        public void Invalidate()
        {
            SelectValid = false;
            EmitValid = false;
            AbsValid = false;
            LegacyValid = false;
        }

        // hit counters for FrameStats / the check log
        public static long SelectHits, SelectMisses, EmitHits, EmitMisses, LegacyHits, LegacyMisses;
    }
}
