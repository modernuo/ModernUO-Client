using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Uo.Pack;

namespace ClassicUO.Game.Data
{
    [InlineArray(PaperdollOrder.N)]
    internal struct LayerUShorts
    {
        private ushort _e;
    }

    [InlineArray(PaperdollOrder.N)]
    internal struct LayerBools
    {
        private bool _e;
    }

    /// <summary>T3b: one wearer's layers OneHanded..Legs as the rule evaluator's tables (an absent layer is 0 / false).</summary>
    internal struct LayerTables
    {
        public LayerBools Present;
        public LayerUShorts Graphic;
        public LayerUShorts Anim;

        public void Gather(Entity entity)
        {
            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                Item item = entity.FindItemByLayer((Layer)l);

                if (item != null)
                {
                    Present[l] = true;
                    Graphic[l] = item.Graphic;
                    Anim[l] = item.ItemData.AnimID;
                }
            }
        }
    }

    /// <summary>
    /// T3b (ecs-transition-plan.md, T3 "Equipment layering as data"): the equipment layering rules from the pack's
    /// layerrules.bin (or, without one, <see cref="LayerRulesDefault.Create"/>, the same source the converter writes): the
    /// worn layers' draw order, the covering and the remaps, for MobileView, the paperdoll, corpses and MobileAnimationSystem.
    /// The hard-coded rules they replaced were deleted with <c>ecs_layer_rules</c> (T9); the unit tests keep a frozen copy
    /// (LegacyLayerRulesReference) to compare the default data with.
    /// </summary>
    internal static class LayerRuleEval
    {
        private static LayerRules _rules;

        public static LayerRules Rules => _rules ??= Load();

        /// <summary>Where the rules came from ("pack" or "built-in").</summary>
        public static string Source { get; private set; } = "built-in";

        /// <summary>Tests: replace the rules (null reloads).</summary>
        internal static void SetRules(LayerRules rules) => _rules = rules;

        private static LayerRules Load()
        {
            try
            {
                var pack = Client.Game?.UO?.FileManager?.Pack;
                string path = pack?.PathOf(LayerRulesConst.FileName);

                if (path != null && File.Exists(path))
                {
                    var rules = LayerRules.Read(path);
                    Source = "pack";
                    Log.Info($"[layers] rules from {path}: {rules.Rules.Length} rules");

                    return rules;
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
            {
                Log.Error($"[layers] layerrules.bin unreadable, using the built-in rules: {e.Message}");
            }

            Source = "built-in";

            return LayerRulesDefault.Create();
        }

        /// <summary>Tests: the client version gate (null: the loaded client's).</summary>
        internal static bool? Cv7000Override;

        private static bool Cv7000 => Cv7000Override ?? (Client.Game?.UO != null && Client.Game.UO.Version >= Utility.ClientVersion.CV_7000);

        public static LayerSubject Subject(ushort body, bool female, bool gargoyleDraw) =>
            new LayerSubject(
                Rules.GroupOf(body),
                (byte)((female ? LayerRulesConst.FlagFemale : 0) | (gargoyleDraw ? LayerRulesConst.FlagGargoyleDraw : 0) | (Cv7000 ? LayerRulesConst.FlagCv7000 : 0)),
                body
            );

        /// <summary>A corpse's wearer: the corpse's body (Amount), and no flags (ItemView draws corpses with altTorsoTable: false,
        /// no covering and no gargoyle remap).</summary>
        public static LayerSubject CorpseSubject(ushort body) => new LayerSubject(Rules.GroupOf(body), 0, body);

        /// <summary>The world order: order, Filter (no backpack), then the direction moves.</summary>
        public static int BuildInWorld(ReadOnlySpan<ushort> key, LayerSubject subject, byte direction, Span<Layer> dest)
        {
            LayerRules rules = Rules;
            Span<byte> raw = stackalloc byte[PaperdollOrder.N];
            rules.BuildOrder(LayerRuleSet.World, key, subject, raw);
            int count = PaperdollOrder.Filter(MemoryMarshal.Cast<byte, Layer>(raw), includeBackpack: false, dest);

            return rules.ApplyDirection(LayerRuleSet.World, MemoryMarshal.Cast<Layer, byte>(dest), count, direction, key, subject);
        }

        /// <summary>The paperdoll's order from data: order, Filter (no backpack).</summary>
        public static int BuildPaperdoll(ReadOnlySpan<ushort> key, LayerSubject subject, Span<Layer> dest)
        {
            Span<byte> raw = stackalloc byte[PaperdollOrder.N];
            Rules.BuildOrder(LayerRuleSet.Paperdoll, key, subject, raw);

            return PaperdollOrder.Filter(MemoryMarshal.Cast<byte, Layer>(raw), includeBackpack: false, dest);
        }

        public static bool IsCovered(LayerRuleSet set, Layer layer, ref LayerTables t, LayerSubject subject) =>
            Rules.IsCovered(set, (byte)layer, t.Present, t.Graphic, t.Anim, subject);

        public static ushort Remap(ushort anim, LayerSubject subject) => Rules.Remap(LayerRuleSet.World, anim, subject);

        /// <summary>
        /// ItemView.DrawCorpse / the corpse pick / ItemSpriteExtract: a corpse's worn layers in paint order, the world set's
        /// order and direction moves for the corpse's body with no flags. Corpses keep no covering and no remap pass.
        /// </summary>
        public static int CorpseOrder(Entity corpse, ushort body, byte direction, Span<Layer> dest)
        {
            Span<ushort> key = stackalloc ushort[PaperdollOrder.N];
            PaperdollOrder.GraphicsFromEntity(corpse, key);

            return BuildInWorld(key, CorpseSubject(body), direction, dest);
        }

        /// <summary>PaperDollInteractable.GetAnimID's remap (the gargoyle ghost's shroud) from the paperdoll set.</summary>
        /// The subject is the paperdoll's (female; gargoyle = the ungated gargoyle bodies, as its altTorso).
        public static ushort RemapPaperdoll(ushort anim, ushort body, bool female) =>
            Rules.Remap(LayerRuleSet.Paperdoll, anim, Subject(body, female, body == 0x029A || body == 0x029B || body == 0x02B6 || body == 0x02B7));
    }

    /// <summary>
    /// T3b: the ECS slots' paint order per facing from the data's direction rules. MobileAnimationSystem's
    /// rebuild applies the world set's DirMove rules to the full layer order for each direction class (0..7, and 8 for any
    /// facing of 8 or more), projects the result onto the drawn slots and interns the 9 permutations here;
    /// <see cref="ClassicUO.Ecs.EquipmentSlots.DirOrder"/> holds the id (0: none, the hard-coded cloak move on the slots).
    /// Few distinct permutations exist (they depend only on which slots are drawn and where the moved layers go), so the
    /// table stays small: <see cref="Stride"/> bytes per distinct entry, kept for the session.
    /// </summary>
    internal static class EquipDirOrders
    {
        public const int Classes = 9;
        public const int Stride = Classes * ClassicUO.Ecs.EquipmentSlots.MAX;

        private static byte[] _data = new byte[Stride * 16];
        private static int _count; // entries; id = index + 1
        private static readonly Dictionary<ulong, int> _byHash = new Dictionary<ulong, int>();
        private static bool _fullLogged;

        public static int Count => _count;

        public static int ClassOf(byte facing) => facing >= 8 ? 8 : facing;

        /// <summary>The permutation (slot indices) of the entry for a direction class.</summary>
        public static ReadOnlySpan<byte> Get(ushort id, int cls, int n) => _data.AsSpan((id - 1) * Stride + cls * ClassicUO.Ecs.EquipmentSlots.MAX, n);

        /// <summary>Interns <paramref name="perms"/> (<see cref="Stride"/> bytes); 0 when the table is full.</summary>
        public static ushort Intern(ReadOnlySpan<byte> perms)
        {
            ulong h = 14695981039346656037UL;

            foreach (byte b in perms)
            {
                h = (h ^ b) * 1099511628211UL;
            }

            if (_byHash.TryGetValue(h, out int found) && _data.AsSpan(found * Stride, Stride).SequenceEqual(perms))
            {
                return (ushort)(found + 1);
            }

            if (_count >= ushort.MaxValue)
            {
                if (!_fullLogged)
                {
                    _fullLogged = true;
                    Log.Error("[layers] direction order table full: the ECS falls back to the hard-coded cloak move");
                }

                return 0;
            }

            if ((_count + 1) * Stride > _data.Length)
            {
                Array.Resize(ref _data, _data.Length * 2);
            }

            perms.CopyTo(_data.AsSpan(_count * Stride, Stride));
            _byHash.TryAdd(h, _count);

            return (ushort)++_count;
        }

        /// <summary>
        /// Rebuild: the 9 permutations of the slots (<paramref name="slotLayers"/>, in slot order) from the full layer order
        /// (<paramref name="fullOrder"/>, PaperdollOrder.Filter's output from the data's order) and the world DirMove rules.
        /// </summary>
        public static ushort Build(ReadOnlySpan<Layer> fullOrder, ReadOnlySpan<byte> slotLayers, ReadOnlySpan<ushort> key, LayerSubject subject)
        {
            const int MAX = ClassicUO.Ecs.EquipmentSlots.MAX;
            Span<byte> perms = stackalloc byte[Stride];
            perms.Clear();
            Span<byte> full = stackalloc byte[PaperdollOrder.N];

            for (int c = 0; c < Classes; c++)
            {
                MemoryMarshal.Cast<Layer, byte>(fullOrder).CopyTo(full);
                int count = LayerRuleEval.Rules.ApplyDirection(LayerRuleSet.World, full, fullOrder.Length, c == 8 ? (byte)8 : (byte)c, key, subject);
                int n = 0;

                for (int i = 0; i < count && n < slotLayers.Length; i++)
                {
                    int slot = slotLayers.IndexOf(full[i]);

                    if (slot >= 0)
                    {
                        perms[c * MAX + n++] = (byte)slot;
                    }
                }
            }

            return Intern(perms);
        }
    }

    [InlineArray(PaperdollOrder.N)]
    internal struct LayerOrderBytes
    {
        private Layer _e;
    }

    [InlineArray(EquipDirOrders.Classes * PaperdollOrder.N)]
    internal struct LayerDirOrderBytes
    {
        private Layer _e;
    }

    /// <summary>
    /// T3b cache: one wearer's full world-set layering answer, reused until an input changes. The answer:
    /// the filtered order (PaperdollOrder.Filter of BuildOrder) and, filled lazily per direction class (0..7, 8 for any facing
    /// of 8 or more), the order after the DirMove rules; the covered flag of every worn layer; the remapped draw AnimID of
    /// every worn layer. The key is everything the rules read: the worn layers' present / graphic / AnimID through the
    /// record's <see cref="ClassicUO.Ecs.WornLayers.Version"/> (slot changes) and <see cref="ClassicUO.Ecs.WornLayers.Look"/>
    /// (any write to a worn item: graphic, hue; the AnimID follows the graphic); the subject compared whole (body, so its
    /// group; the female, gargoyle-draw and 7.0.0.0 flags); the rules instance (fixed once loaded; tests swap it). The
    /// direction only selects the class. A hit with no equipment mark anywhere since the last check (the mirror-wide
    /// <see cref="ClassicUO.Ecs.WorldMirror.EquipChanges"/>) is a handful of compares; after a mark anywhere, one component
    /// read of Look. Held by the mobile's <see cref="WornCache.Layers"/>, made at its first use.
    /// </summary>
    internal sealed class LayerDecision
    {
        private const int N = PaperdollOrder.N;

        // key
        private LayerRules _rules;
        private uint _subject; // LayerSubject as 4 bytes
        private uint _look;
        private ulong _version;
        private ulong _seenChanges;

        // answer
        private byte _count;
        private ushort _dirValid;
        private uint _covered;
        private LayerOrderBytes _base;
        private LayerDirOrderBytes _dir;
        private LayerUShorts _anim; // the order key (worn AnimIDs) for the lazy direction moves
        private LayerUShorts _remap;

        /// <summary>Cache hits and misses (the 5 s checker summary, tests).</summary>
        public static long Hits, Misses;

        /// <summary>The object's size (layout-based; a test measures it): the memory summary counts it per mobile.</summary>
        public const int Bytes = 408;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Pack(LayerSubject s) => Unsafe.As<LayerSubject, uint>(ref s);

        public LayerSubject Subject
        {
            get
            {
                uint s = _subject;

                return Unsafe.As<uint, LayerSubject>(ref s);
            }
        }

        /// <summary>The fast check: the same rules, subject and slot version, and no equipment mark in the mirror since.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCurrent(LayerRules rules, LayerSubject subject, ulong version, ulong changes) =>
            _seenChanges == changes && _version == version && _subject == Pack(subject) && ReferenceEquals(_rules, rules);

        /// <summary>
        /// After a mark somewhere: true (a hit) when the key still matches with the wearer's <paramref name="look"/>; else
        /// takes the new key and returns false: the caller fills the answer (<see cref="Fill"/>) before using it.
        /// </summary>
        public bool Revalidate(LayerRules rules, LayerSubject subject, ulong version, uint look, ulong changes)
        {
            if (_version == version && _look == look && _subject == Pack(subject) && ReferenceEquals(_rules, rules))
            {
                _seenChanges = changes;

                return true;
            }

            _rules = rules;
            _subject = Pack(subject);
            _version = version;
            _look = look;
            _seenChanges = changes;
            _dirValid = 0;

            return false;
        }

        /// <summary>
        /// The cache step of <c>WorldState.WorldLayers</c>: true when the answer is current for the key (the record's
        /// slot <paramref name="version"/>, its Look read only after a mark somewhere in <paramref name="mirror"/>); false
        /// when the key moved: the key is taken and the caller gathers the wearer's tables and calls <see cref="Fill"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Lookup(LayerRules rules, LayerSubject subject, ulong version, ClassicUO.Ecs.WorldMirror mirror, Friflo.Engine.ECS.Entity record)
        {
            ulong changes = mirror.EquipChanges;

            if (IsCurrent(rules, subject, version, changes)
                || Revalidate(rules, subject, version, record.GetComponent<ClassicUO.Ecs.EquipmentSlots>().Worn.Look, changes))
            {
                Hits++;

                return true;
            }

            Misses++;

            return false;
        }

        /// <summary>Tests and the bench: forget the answer (the next check misses).</summary>
        public void Invalidate() => _rules = null;

        /// <summary>The answer for the wearer's tables (the rules and subject from the key just taken).</summary>
        public void Fill(ref LayerTables t)
        {
            LayerRules rules = _rules;
            LayerSubject subject = Subject;
            Span<byte> raw = stackalloc byte[N];
            rules.BuildOrder(LayerRuleSet.World, t.Anim, subject, raw);
            _count = (byte)PaperdollOrder.Filter(MemoryMarshal.Cast<byte, Layer>(raw), includeBackpack: false, _base);
            _dirValid = 0;
            _anim = t.Anim;
            uint covered = 0;

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                if (!t.Present[l])
                {
                    _remap[l] = 0;

                    continue;
                }

                if (rules.IsCovered(LayerRuleSet.World, (byte)l, t.Present, t.Graphic, t.Anim, subject))
                {
                    covered |= 1u << l;
                }

                ushort anim = t.Anim[l];
                _remap[l] = anim == 0 ? (ushort)0 : rules.Remap(LayerRuleSet.World, anim, subject);
            }

            _covered = covered;
        }

        /// <summary>The paint order for a facing (BuildInWorld's answer): the direction class's, computed at its first use.</summary>
        public ReadOnlySpan<Layer> Order(byte direction)
        {
            int c = direction >= 8 ? 8 : direction;
            Span<Layer> o = ((Span<Layer>)_dir).Slice(c * N, N);

            if ((_dirValid & (1 << c)) == 0)
            {
                ((ReadOnlySpan<Layer>)_base).CopyTo(o);
                _rules.ApplyDirection(LayerRuleSet.World, MemoryMarshal.Cast<Layer, byte>(o), _count, (byte)c, _anim, Subject);
                _dirValid |= (ushort)(1 << c);
            }

            return o.Slice(0, _count);
        }

        /// <summary>A worn layer's covered flag (false for a layer not worn when filled).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCovered(Layer layer) => (uint)layer < N && (_covered & (1u << (int)layer)) != 0;

        /// <summary>A worn layer's draw AnimID after the remap rules (0 for a layer not worn or without an AnimID).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort Remap(Layer layer) => (uint)layer < N ? _remap[(int)layer] : (ushort)0;

#if CUO_DEVTOOLS
        /// <summary>
        /// <c>CUO_LAYER_CACHE_CHECK</c>: takes a key without a lookup, for a scratch decision the checker fills with a fresh
        /// evaluation (never used for <see cref="Lookup"/>).
        /// </summary>
        internal void Reset(LayerRules rules, LayerSubject subject)
        {
            _rules = rules;
            _subject = Pack(subject);
            _version = 0;
            _look = 0;
            _seenChanges = 0;
            _dirValid = 0;
        }

        /// <summary>
        /// <c>CUO_LAYER_CACHE_CHECK</c>: the first difference between this answer and <paramref name="other"/> (the subject,
        /// the order for each direction class, each worn layer's covered flag and remap); null when they are the same.
        /// Allocates only for a difference.
        /// </summary>
        internal string Difference(LayerDecision other)
        {
            if (_subject != other._subject)
            {
                return $"subject 0x{_subject:X8} | fresh 0x{other._subject:X8}";
            }

            for (byte d = 0; d <= 8; d++)
            {
                ReadOnlySpan<Layer> mine = Order(d), theirs = other.Order(d);

                if (!mine.SequenceEqual(theirs))
                {
                    return $"order (direction {d}) [{Fmt(mine)}] | fresh [{Fmt(theirs)}]";
                }
            }

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                if (IsCovered((Layer)l) != other.IsCovered((Layer)l))
                {
                    return $"covered {(Layer)l} {IsCovered((Layer)l)} | fresh {other.IsCovered((Layer)l)}";
                }

                if (Remap((Layer)l) != other.Remap((Layer)l))
                {
                    return $"remap {(Layer)l} 0x{Remap((Layer)l):X4} | fresh 0x{other.Remap((Layer)l):X4}";
                }
            }

            return null;
        }

        private static string Fmt(ReadOnlySpan<Layer> order)
        {
            var sb = new System.Text.StringBuilder();

            foreach (Layer l in order)
            {
                sb.Append(((byte)l).ToString("X2")).Append(' ');
            }

            return sb.ToString().TrimEnd();
        }
#endif
    }

#if CUO_DEVTOOLS
    /// <summary>
    /// <c>CUO_LAYER_CACHE_CHECK</c> (CUO_DEVTOOLS builds only; restores the cache half of the deleted
    /// <c>CUO_LAYER_RULES_CHECK</c>): on a <see cref="LayerDecision"/> cache hit in <c>WorldState.WorldLayers</c>, the
    /// cached answer is compared with a fresh evaluation of the rules on the wearer's tables (a scratch decision, no
    /// allocation): a key input that moved without invalidating shows as "[layers] cache mismatch". <c>=1</c> checks every
    /// hit, <c>=N</c> every Nth. A summary line every 5 s while the counts change. CLEANUP(release).
    /// </summary>
    internal static class LayerCacheCheck
    {
        /// <summary>Check every Nth hit (0: off). Tests set it.</summary>
        public static int Every = ParseEvery(Environment.GetEnvironmentVariable("CUO_LAYER_CACHE_CHECK"));

        public static long Hits, Checks, Mismatches;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal static Action<string> MismatchObserver;

        private const int MAX_LOGGED = 25;
        private static readonly LayerDecision _fresh = new LayerDecision();
        private static long _logged;
        private static uint _summaryAt;
        private static long _summaryChecks = -1;

        internal static int ParseEvery(string value) => int.TryParse(value, out int n) && n > 0 ? n : 0;

        public static bool Enabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Every > 0;
        }

        /// <summary><c>WorldState.WorldLayers</c> on a hit: every Nth, the cached answer against the wearer's tables now.</summary>
        public static void OnHit(Mobile mobile, LayerDecision cached, LayerRules rules, LayerSubject subject)
        {
            if (++Hits % Every != 0)
            {
                return;
            }

            LayerTables t = default;
            t.Gather(mobile);
            Check(mobile.Serial, cached, ref t, rules, subject);
        }

        /// <summary>The comparison (tests call it with tables of their own): true when the cached answer is the fresh one.</summary>
        public static bool Check(uint serial, LayerDecision cached, ref LayerTables t, LayerRules rules, LayerSubject subject)
        {
            Checks++;
            _fresh.Reset(rules, subject);
            _fresh.Fill(ref t);
            string diff = cached.Difference(_fresh);

            if (diff == null)
            {
                return true;
            }

            Mismatches++;
            string message = $"[layers] cache mismatch 0x{serial:X8}: {diff}";
            MismatchObserver?.Invoke(message);

            if (_logged++ < MAX_LOGGED)
            {
                Log.Warn(message);
            }

            return false;
        }

        /// <summary>The 5 s summary while the counts change (called every frame by <c>WorldEcs.Update</c>).</summary>
        public static void LogSummary()
        {
            if (!Enabled || Time.Ticks < _summaryAt || Checks == _summaryChecks)
            {
                return;
            }

            _summaryAt = Time.Ticks + 5000;
            _summaryChecks = Checks;
            Log.Info($"[layers] cache checks {Checks} of {Hits} hits (every {Every}) mismatches {Mismatches} | decision hits {LayerDecision.Hits} misses {LayerDecision.Misses}");
        }
    }
#endif
}
