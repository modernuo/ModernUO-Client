// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Uo.Pack;
using Uo.Render;

namespace ClassicUO.Assets
{
    /// <summary>
    /// Mobile, equipment and corpse animations from the pack (anim/anim.bin, anim/palettes.bin, equipconv.bin). The
    /// converter already resolved Body.def, Bodyconv.def, Corpse.def, mobtypes.txt and AnimationSequence.uop.
    /// </summary>
    public sealed class AnimationsLoader : UOFileLoader
    {
        public const int MAX_ACTIONS = 80; // gargoyle is like 78
        public const int MAX_DIRECTIONS = 5;

        private const int MAX_CACHED_PALETTES = 8192;

        private readonly Dictionary<ushort, Dictionary<ushort, EquipConvData>> _equipConv = new Dictionary<ushort, Dictionary<ushort, EquipConvData>>();
        private readonly LruCache<uint, uint[]> _palettes = new LruCache<uint, uint[]>(MAX_CACHED_PALETTES);
        private uint[] _spare;
        private AnimBodyEntry[] _bodies = Array.Empty<AnimBodyEntry>();
        private AnimDirEntry[] _dirs = Array.Empty<AnimDirEntry>();
        private AnimFrameEntry[] _frames = Array.Empty<AnimFrameEntry>();
        private uint[] _framePalette = Array.Empty<uint>();
        private long _paletteDataOffset;
        private int _paletteCount;
        private SafeFileHandle _paletteFile;

        public AnimationsLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public IReadOnlyDictionary<ushort, Dictionary<ushort, EquipConvData>> EquipConversions => _equipConv;

        public List<(ushort, byte)>[] GroupReplaces { get; } =
            new List<(ushort, byte)>[2]
            {
                new List<(ushort, byte)>(),
                new List<(ushort, byte)>()
            };

        public int BodyCount => _bodies.Length;

        public AnimBodyEntry[] PackBodies => _bodies;

        public AnimDirEntry[] PackDirs => _dirs;

        public AnimFrameEntry[] PackFrames => _frames;

        /// <summary>anim/palettes.bin's frame → palette table, shared with the GPU world renderer (PackTables).</summary>
        public uint[] PackFramePalette => _framePalette;

        public override void Load()
        {
            (_bodies, _dirs, _frames) = AnimTableFile.Read(FileManager.Pack.PathOf("anim/anim.bin"));

            var palettePath = FileManager.Pack.PathOf("anim/palettes.bin");

            // validated: counts, payload extent against the file length, palette ids (shared with the GPU world renderer)
            _framePalette = AnimPaletteFile.ReadFramePalette(palettePath, out _paletteCount, out _paletteDataOffset);

            if (_framePalette.Length != _frames.Length)
            {
                throw new InvalidDataException("anim/palettes.bin does not match anim/anim.bin");
            }

            _paletteFile?.Dispose();
            _paletteFile = File.OpenHandle(palettePath);

            _equipConv.Clear();

            foreach (var e in EquipConvFile.Read(FileManager.Pack.PathOf(TableConst.EquipConvFile)))
            {
                if (!_equipConv.TryGetValue(e.Body, out var dict))
                {
                    _equipConv[e.Body] = dict = new Dictionary<ushort, EquipConvData>();
                }

                dict[e.Graphic] = new EquipConvData(e.NewGraphic, e.Gump, e.Color);
            }

            LoadGroupReplaces("Anim1.def", GroupReplaces[0]);
            LoadGroupReplaces("Anim2.def", GroupReplaces[1]);
        }

        private void LoadGroupReplaces(string name, List<(ushort, byte)> list)
        {
            list.Clear();

            string file = FileManager.GetUOFilePath(name);

            if (!File.Exists(file))
            {
                return;
            }

            using var defReader = new DefReader(file);

            while (defReader.Next())
            {
                ushort group = (ushort)defReader.ReadInt();

                if (group == 0xFFFF)
                {
                    continue;
                }

                int replace = defReader.ReadGroupInt();

                list.Add((group, (byte)replace));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly AnimBodyEntry GetBody(ushort body) => ref _bodies[body];

        /// <summary>The direction record of (body, action, UO direction 0..7), without following aliases.</summary>
        public bool TryGetDirection(ushort body, int action, int direction, out AnimDirEntry entry)
        {
            entry = default;

            if (body >= _bodies.Length || action < 0 || action >= _bodies[body].ActionCount)
            {
                return false;
            }

            entry = _dirs[_bodies[body].FirstDir + action * AnimConst.Directions + (direction & 7)];

            return entry.FrameCount != 0;
        }

        /// <summary>Applies the converter-resolved Body.def (or Corpse.def) alias; <paramref name="hue"/> is its hue, else 0.</summary>
        public ushort ResolveBody(ushort body, bool isCorpse, out ushort hue)
        {
            hue = 0;

            if (body >= _bodies.Length)
            {
                return body;
            }

            ref readonly var entry = ref _bodies[body];
            ushort alias = isCorpse ? entry.CorpseBody : entry.AliasBody;

            if (alias == AnimConst.NoBody || alias >= _bodies.Length)
            {
                return body;
            }

            hue = isCorpse ? entry.CorpseHue : entry.AliasHue;

            return alias;
        }

        public ref readonly AnimFrameEntry GetFrame(uint index) => ref _frames[index];

        /// <summary>Decodes frame <paramref name="index"/> to RGBA8 at its full (untrimmed) size.</summary>
        public uint[] DecodeFrame(uint index)
        {
            ref readonly var frame = ref _frames[index];
            var pixels = new uint[frame.Sprite.OrigW * frame.Sprite.OrigH];
            DecodeFrame(index, pixels);

            return pixels;
        }

        /// <summary>
        /// Decodes frame <paramref name="index"/> into <paramref name="pixels"/> (at least OrigW x OrigH). Thread-safe:
        /// animation directions decode on worker threads.
        /// </summary>
        public void DecodeFrame(uint index, Span<uint> pixels)
        {
            ref readonly var frame = ref _frames[index];

            if (!frame.Sprite.Present)
            {
                pixels.Slice(0, frame.Sprite.OrigW * frame.Sprite.OrigH).Clear();

                return;
            }

            // an evicted palette array is reused, so copy it under the lock and decode (maybe a chunk decompress) outside it
            Span<uint> palette = stackalloc uint[AnimPaletteFile.Entries];

            lock (_palettes)
            {
                GetPaletteLocked(_framePalette[index]).CopyTo(palette);
            }

            PackSprites.DecodeIndexed(FileManager.Pack.Anim, in frame.Sprite, palette, pixels);
        }

        /// <summary>True when decoding frame <paramref name="index"/> needs no chunk decompress (its texels are cached, or it has none).</summary>
        public bool IsFrameResident(uint index)
        {
            ref readonly var s = ref _frames[index].Sprite;

            return !s.Present || s.W == 0 || s.H == 0 || FileManager.Pack.Anim.IsResident(s.Page, s.X, s.Y, s.W, s.H);
        }

        /// <summary>Decompresses the chunks under frame <paramref name="index"/> into the cache (thread-safe).</summary>
        public void PrefetchFrame(uint index)
        {
            ref readonly var s = ref _frames[index].Sprite;

            if (s.Present && s.W != 0 && s.H != 0)
            {
                FileManager.Pack.Anim.Touch(s.Page, s.X, s.Y, s.W, s.H);
            }
        }

        private uint[] GetPaletteLocked(uint id)
        {
            if (_palettes.TryGet(id, out var palette))
            {
                return palette;
            }

            if (id >= (uint)_paletteCount)
            {
                throw new InvalidDataException($"anim palette {id} out of range");
            }

            Span<ushort> raw = stackalloc ushort[AnimPaletteFile.Entries];
            RandomAccess.Read(_paletteFile, MemoryMarshal.AsBytes(raw), _paletteDataOffset + (long)id * AnimPaletteFile.Entries * 2);
            palette = _spare ?? new uint[AnimPaletteFile.Entries];
            _spare = null;
            PixelExpand.Expand(raw, palette);
            palette[0] = 0;

            // least recently used palette out (it used to clear all 8,192 at once); its array is reused for the next miss
            if (_palettes.Add(id, palette, out var evicted))
            {
                _spare = evicted;
            }

            return palette;
        }

        public override void ClearResources()
        {
            _paletteFile?.Dispose();
            _paletteFile = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GetSittingAnimDirection(ref byte dir, ref bool mirror, ref int x, ref int y)
        {
            switch (dir)
            {
                case 0:
                    mirror = true;
                    dir = 3;

                    break;

                case 2:
                    mirror = true;
                    dir = 1;

                    break;

                case 4:
                    mirror = false;
                    dir = 1;

                    break;

                case 6:
                    mirror = false;
                    dir = 3;

                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FixSittingDirection(
            ref byte direction,
            ref bool mirror,
            ref int x,
            ref int y,
            ref SittingInfoData data
        )
        {
            switch (direction)
            {
                case 7:
                case 0:
                    {
                        if (data.Direction1 == -1)
                        {
                            if (direction == 7)
                            {
                                direction = (byte)data.Direction4;
                            }
                            else
                            {
                                direction = (byte)data.Direction2;
                            }
                        }
                        else
                        {
                            direction = (byte)data.Direction1;
                        }

                        break;
                    }

                case 1:
                case 2:
                    {
                        if (data.Direction2 == -1)
                        {
                            if (direction == 1)
                            {
                                direction = (byte)data.Direction1;
                            }
                            else
                            {
                                direction = (byte)data.Direction3;
                            }
                        }
                        else
                        {
                            direction = (byte)data.Direction2;
                        }

                        break;
                    }

                case 3:
                case 4:
                    {
                        if (data.Direction3 == -1)
                        {
                            if (direction == 3)
                            {
                                direction = (byte)data.Direction2;
                            }
                            else
                            {
                                direction = (byte)data.Direction4;
                            }
                        }
                        else
                        {
                            direction = (byte)data.Direction3;
                        }

                        break;
                    }

                case 5:
                case 6:
                    {
                        if (data.Direction4 == -1)
                        {
                            if (direction == 5)
                            {
                                direction = (byte)data.Direction3;
                            }
                            else
                            {
                                direction = (byte)data.Direction1;
                            }
                        }
                        else
                        {
                            direction = (byte)data.Direction4;
                        }

                        break;
                    }
            }

            GetSittingAnimDirection(ref direction, ref mirror, ref x, ref y);

            const int SITTING_OFFSET_X = 8;

            int offsX = SITTING_OFFSET_X;

            if (mirror)
            {
                if (direction == 3)
                {
                    y += 25 + data.MirrorOffsetY;
                    x += offsX - 4;
                }
                else
                {
                    y += data.OffsetY + 9;
                }
            }
            else
            {
                if (direction == 3)
                {
                    y += 23 + data.MirrorOffsetY;
                    x -= 3;
                }
                else
                {
                    y += 10 + data.OffsetY;
                    x -= offsX + 1;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationGroups GetGroupIndex(ushort graphic, AnimationGroupsType animType)
        {
            switch (animType)
            {
                case AnimationGroupsType.Animal:
                    return AnimationGroups.Low;

                case AnimationGroupsType.Monster:
                case AnimationGroupsType.SeaMonster:
                    return AnimationGroups.High;

                case AnimationGroupsType.Human:
                case AnimationGroupsType.Equipment:
                    return AnimationGroups.People;
            }

            return AnimationGroups.High;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetDeathAction(
            ushort animID,
            AnimationFlags animFlags,
            AnimationGroupsType animType,
            bool second,
            bool isRunning = false
        )
        {
            //ConvertBodyIfNeeded(ref animID);

            if (animFlags.HasFlag(AnimationFlags.CalculateOffsetByLowGroup))
            {
                animType = AnimationGroupsType.Animal;
            }

            if (animFlags.HasFlag(AnimationFlags.CalculateOffsetLowGroupExtended))
            {
                animType = AnimationGroupsType.Monster;
            }

            switch (animType)
            {
                case AnimationGroupsType.Animal:

                    if (
                        (animFlags & AnimationFlags.Use2IfHittedWhileRunning) != 0
                        || (animFlags & AnimationFlags.CanFlying) != 0
                    )
                    {
                        return 2;
                    }

                    if ((animFlags & AnimationFlags.UseUopAnimation) != 0)
                    {
                        return (byte)(second ? 3 : 2);
                    }

                    return (byte)(
                        second ? LowAnimationGroup.Die2 : LowAnimationGroup.Die1
                    );

                case AnimationGroupsType.SeaMonster:
                    {
                        if (!isRunning)
                        {
                            return 8;
                        }

                        goto case AnimationGroupsType.Monster;
                    }

                case AnimationGroupsType.Monster:

                    if ((animFlags & AnimationFlags.UseUopAnimation) != 0)
                    {
                        return (byte)(second ? 3 : 2);
                    }

                    return (byte)(
                        second ? HighAnimationGroup.Die2 : HighAnimationGroup.Die1
                    );

                case AnimationGroupsType.Human:
                case AnimationGroupsType.Equipment:
                    return (byte)(
                        second ? PeopleAnimationGroup.Die2 : PeopleAnimationGroup.Die1
                    );
            }

            return 0;
        }

        public struct SittingInfoData
        {
            public SittingInfoData(
                ushort graphic,
                sbyte d1,
                sbyte d2,
                sbyte d3,
                sbyte d4,
                sbyte offsetY,
                sbyte mirrorOffsetY,
                bool drawback
            )
            {
                Graphic = graphic;
                Direction1 = d1;
                Direction2 = d2;
                Direction3 = d3;
                Direction4 = d4;
                OffsetY = offsetY;
                MirrorOffsetY = mirrorOffsetY;
                DrawBack = drawback;
            }

            public readonly ushort Graphic;
            public readonly sbyte Direction1,
                Direction2,
                Direction3,
                Direction4;
            public readonly sbyte OffsetY,
                MirrorOffsetY;
            public readonly bool DrawBack;

            public static SittingInfoData Empty = new SittingInfoData();
        }

    }

    public enum AnimationGroups
    {
        None = 0,
        Low,
        High,
        People
    }

    public enum AnimationGroupsType
    {
        Monster = 0,
        SeaMonster,
        Animal,
        Human,
        Equipment,
        Unknown
    }

    public enum HighAnimationGroup
    {
        Walk = 0,
        Stand,
        Die1,
        Die2,
        Attack1,
        Attack2,
        Attack3,
        Misc1,
        Misc2,
        Misc3,
        Stumble,
        SlapGround,
        Cast,
        GetHit1,
        Misc4,
        GetHit2,
        GetHit3,
        Fidget1,
        Fidget2,
        Fly,
        Land,
        DieInFlight,
        AnimationCount
    }

    public enum PeopleAnimationGroup
    {
        WalkUnarmed = 0,
        WalkArmed,
        RunUnarmed,
        RunArmed,
        Stand,
        Fidget1,
        Fidget2,
        StandOnehandedAttack,
        StandTwohandedAttack,
        AttackOnehanded,
        AttackUnarmed1,
        AttackUnarmed2,
        AttackTwohandedDown,
        AttackTwohandedWide,
        AttackTwohandedJab,
        WalkWarmode,
        CastDirected,
        CastArea,
        AttackBow,
        AttackCrossbow,
        GetHit,
        Die1,
        Die2,
        OnmountRideSlow,
        OnmountRideFast,
        OnmountStand,
        OnmountAttack,
        OnmountAttackBow,
        OnmountAttackCrossbow,
        OnmountSlapHorse,
        Turn,
        AttackUnarmedAndWalk,
        EmoteBow,
        EmoteSalute,
        Fidget3,
        AnimationCount
    }

    public enum LowAnimationGroup
    {
        Walk = 0,
        Run,
        Stand,
        Eat,
        Unknown,
        Attack1,
        Attack2,
        Attack3,
        Die1,
        Fidget1,
        Fidget2,
        LieDown,
        Die2,
        AnimationCount
    }

    [Flags]
    public enum AnimationFlags : uint
    {
        None = 0x00000,
        Unknown1 = 0x00001,
        Use2IfHittedWhileRunning = 0x00002,
        IdleAt8Frame = 0x00004,
        CanFlying = 0x00008,
        Unknown10 = 0x00010,
        CalculateOffsetLowGroupExtended = 0x00020,
        CalculateOffsetByLowGroup = 0x00040,
        Unknown80 = 0x00080,
        Unknown100 = 0x00100,
        Unknown200 = 0x00200,
        CalculateOffsetByPeopleGroup = 0x00400,
        Unknown800 = 0x00800,
        Unknown1000 = 0x01000,
        Unknown2000 = 0x02000,
        Unknown4000 = 0x04000,
        Unknown8000 = 0x08000,
        UseUopAnimation = 0x10000,
        Unknown20000 = 0x20000,
        Unknown40000 = 0x40000,
        Unknown80000 = 0x80000,
        Found = 0x80000000
    }

    public struct EquipConvData : IEquatable<EquipConvData>
    {
        public EquipConvData(ushort graphic, ushort gump, ushort color)
        {
            Graphic = graphic;
            Gump = gump;
            Color = color;
        }

        public ushort Graphic;
        public ushort Gump;
        public ushort Color;

        public override int GetHashCode()
        {
            return (Graphic, Gump, Color).GetHashCode();
        }

        public override bool Equals(object obj)
        {
            return obj is EquipConvData v && Equals(v);
        }

        public bool Equals(EquipConvData other)
        {
            return (Graphic, Gump, Color) == (other.Graphic, other.Gump, other.Color);
        }
    }
}
