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
    /// T3b (ecs-transition-plan.md, T3 "Equipment layering as data"): the layering rules from the pack's layerrules.bin
    /// (or, without one, <see cref="LayerRulesDefault.Create"/>, the same source the converter writes), used instead of
    /// PaperdollOrder.Build/ApplyDirectionCloak, MobileView.IsCovered and FixGargoyleEquipments with <c>ecs_layer_rules</c> on
    /// (default off). <c>CUO_LAYER_RULES_CHECK=1</c> (CUO_DEVTOOLS builds) computes both answers at every decision.
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

        // CLEANUP(release): the gate's override of the setting (CUO_ECS_LAYER_RULES=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_ECS_LAYER_RULES") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        /// <summary>Tests and the session driver.</summary>
        internal static bool? Override;

        public static bool On => Override ?? _env ?? Configuration.Settings.GlobalSettings.EcsLayerRules;

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

        /// <summary>PaperdollOrder.BuildInWorld from data: order, Filter (no backpack), then the direction moves.</summary>
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
        /// ItemView.DrawCorpse / the corpse pick / ItemSpriteExtract: a corpse's worn layers in paint order. With ecs_layer_rules
        /// the world set's order and direction moves for the corpse's body with no flags (altTorsoTable: false); else
        /// PaperdollOrder.BuildInWorld. Corpses keep no covering and no remap pass.
        /// </summary>
        public static int CorpseOrder(Entity corpse, ushort body, byte direction, Span<Layer> dest)
        {
            bool data = On;

            if (!data && !CheckEnabled)
            {
                return PaperdollOrder.BuildInWorld(corpse, altTorsoTable: false, direction, dest);
            }

            Span<ushort> key = stackalloc ushort[PaperdollOrder.N];
            PaperdollOrder.GraphicsFromEntity(corpse, key);
            int count = data ? BuildInWorld(key, CorpseSubject(body), direction, dest) : PaperdollOrder.BuildInWorld(corpse, altTorsoTable: false, direction, dest);

            if (CheckEnabled)
            {
                Span<Layer> other = stackalloc Layer[PaperdollOrder.N];
                int oc = data ? PaperdollOrder.BuildInWorld(corpse, altTorsoTable: false, direction, other) : BuildInWorld(key, CorpseSubject(body), direction, other);
                ReadOnlySpan<Layer> hard = data ? other.Slice(0, oc) : dest.Slice(0, count);
                ReadOnlySpan<Layer> fromData = data ? dest.Slice(0, count) : other.Slice(0, oc);
                CompareOrder("corpse", corpse.Serial, hard, fromData);
            }

            return count;
        }

        /// <summary>PaperDollInteractable.GetAnimID's remap (the gargoyle ghost's shroud) from the paperdoll set.</summary>
        /// The subject is the paperdoll's (female; gargoyle = the ungated gargoyle bodies, as its altTorso).
        public static ushort RemapPaperdoll(ushort anim, ushort body, bool female) =>
            Rules.Remap(LayerRuleSet.Paperdoll, anim, Subject(body, female, body == 0x029A || body == 0x029B || body == 0x02B6 || body == 0x02B7));

        /// <summary>The hard-coded GetAnimID remap, for CUO_LAYER_RULES_CHECK and the tests.</summary>
        public static ushort RemapPaperdollHard(ushort anim, ushort body, bool cv7000) =>
            cv7000 && anim == 0x03CA && (body == 0x02B7 || body == 0x02B6) ? (ushort)0x0223 : anim;

        // ---- CUO_LAYER_RULES_CHECK (CUO_DEVTOOLS builds only) -----------------------------------------------------------
        // Every covered/order decision in the world (MobileView draw and pick, MobileAnimationSystem) and the paperdoll
        // computes the hard-coded and the data-driven answer and logs "[layers] mismatch ...". CLEANUP(release): with the
        // hard-coded rules (T9).
#if CUO_DEVTOOLS
        public static bool CheckEnabled = Environment.GetEnvironmentVariable("CUO_LAYER_RULES_CHECK") == "1";
#else
        public const bool CheckEnabled = false;
#endif

        public static long Checks, Mismatches;
        private static long _logged;
        private static uint _summaryAt;
        private static long _summaryChecks = -1;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal static Action<string> MismatchObserver;

        private static void Report(string message)
        {
            Mismatches++;
            MismatchObserver?.Invoke(message);

            if (_logged++ < 200)
            {
                Log.Warn(message);
            }
        }

        /// <summary>5 s summary, from WorldEcs.Update.</summary>
        public static void LogSummary()
        {
            if (!CheckEnabled || Time.Ticks < _summaryAt || Checks == _summaryChecks)
            {
                return;
            }

            _summaryAt = Time.Ticks + 5000;
            _summaryChecks = Checks;
            Log.Info($"[layers] checks {Checks} mismatches {Mismatches} rules {Source} ecs_layer_rules={(On ? "on" : "off")}");
        }

        private static string Fmt(ReadOnlySpan<Layer> a)
        {
            var sb = new System.Text.StringBuilder();
            foreach (Layer l in a) sb.Append(((byte)l).ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        private static void CompareOrder(string where, uint serial, ReadOnlySpan<Layer> hard, ReadOnlySpan<Layer> data)
        {
            Checks++;

            if (!hard.SequenceEqual(data))
            {
                Report($"[layers] mismatch ({where} order) 0x{serial:X8}: hard [{Fmt(hard)}] | data [{Fmt(data)}]");
            }
        }

        private static void CompareCovered(string where, uint serial, Layer layer, bool hard, bool data, ref LayerTables t)
        {
            Checks++;

            if (hard != data)
            {
                Report($"[layers] mismatch ({where} covered) 0x{serial:X8} layer {layer}: hard {hard} | data {data} | robe 0x{t.Graphic[(int)Layer.Robe]:X4}/0x{t.Anim[(int)Layer.Robe]:X4}");
            }
        }

        /// <summary>MobileView's draw: the order (BuildInWorld), every layer's IsCovered, and the gargoyle remap.</summary>
        public static void CheckWorld(Mobile mobile, bool gargoyleDraw, byte direction)
        {
            LayerTables t = default;
            t.Gather(mobile);
            LayerSubject subject = Subject(mobile.Graphic, mobile.IsFemale, gargoyleDraw);

            Span<Layer> hard = stackalloc Layer[PaperdollOrder.N];
            Span<Layer> data = stackalloc Layer[PaperdollOrder.N];
            int hc = PaperdollOrder.BuildInWorld(mobile, mobile.IsFemale || gargoyleDraw, direction, hard);
            int dc = BuildInWorld(t.Anim, subject, direction, data);
            CompareOrder("world", mobile.Serial, hard.Slice(0, hc), data.Slice(0, dc));

            CheckCovered("world", mobile, LayerRuleSet.World, ref t, subject);

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                ushort anim = t.Anim[l];

                // every worn AnimID: the data's remap conditions decide whom it applies to
                if (anim != 0)
                {
                    ushort h = anim;

                    if (gargoyleDraw)
                    {
                        Mobile.FixGargoyleEquipmentsHard(ref h);
                    }

                    ushort d = Remap(anim, subject);
                    Checks++;

                    if (h != d)
                    {
                        Report($"[layers] mismatch (world remap) 0x{mobile.Serial:X8} anim 0x{anim:X4}: hard 0x{h:X4} | data 0x{d:X4}");
                    }
                }
            }
        }

        /// <summary>MobileView's pick: one worn layer's IsCovered.</summary>
        public static void CheckPick(Mobile mobile, Layer layer, ref LayerTables t, LayerSubject subject)
        {
            CompareCovered("pick", mobile.Serial, layer, Mobile.IsCovered(mobile, layer), IsCovered(LayerRuleSet.World, layer, ref t, subject), ref t);
        }

        /// <summary>MobileView's pick: the graphic the hit test resolves (remap, then the equip conversion), from both helpers.</summary>
        public static void ComparePickGraphic(uint serial, Layer layer, ushort hard, ushort data)
        {
            Checks++;

            if (hard != data)
            {
                Report($"[layers] mismatch (pick graphic) 0x{serial:X8} layer {layer}: hard 0x{hard:X4} | data 0x{data:X4}");
            }
        }

        /// <summary>The paperdoll's gump remap (PaperDollInteractable.GetAnimID), both answers.</summary>
        public static void ComparePaperdollRemap(ushort body, ushort anim, bool female, ushort used)
        {
            ushort hard = RemapPaperdollHard(anim, body, Cv7000), data = RemapPaperdoll(anim, body, female);
            Checks++;

            if (hard != data || used != (On ? data : hard))
            {
                Report($"[layers] mismatch (paperdoll remap) body 0x{body:X4} anim 0x{anim:X4}: hard 0x{hard:X4} | data 0x{data:X4} | used 0x{used:X4}");
            }
        }

        /// <summary>
        /// MobileAnimationSystem's rebuilt slots: the final paint order (AnimationRules.PaintOrder, what MobileSpriteExtract
        /// draws) for every direction class against the hard-coded world order (PaperdollOrder.Build, Filter,
        /// ApplyDirectionCloak) restricted to the drawn layers.
        /// </summary>
        public static void CheckEcsSlots(uint serial, ReadOnlySpan<ushort> key, bool altTorso, in ClassicUO.Ecs.EquipmentSlots eq)
        {
            int n = eq.Count;
            Span<Layer> raw = stackalloc Layer[PaperdollOrder.N];
            Span<Layer> full = stackalloc Layer[PaperdollOrder.N];
            Span<byte> slotLayers = stackalloc byte[ClassicUO.Ecs.EquipmentSlots.MAX];
            Span<Layer> hard = stackalloc Layer[ClassicUO.Ecs.EquipmentSlots.MAX];
            Span<Layer> got = stackalloc Layer[ClassicUO.Ecs.EquipmentSlots.MAX];
            Span<byte> order = stackalloc byte[ClassicUO.Ecs.EquipmentSlots.MAX];

            for (int i = 0; i < n; i++)
            {
                slotLayers[i] = eq.Slots[i].Layer;
            }

            PaperdollOrder.Build(key, altTorso, raw);

            for (int c = 0; c < EquipDirOrders.Classes; c++)
            {
                byte dir = (byte)c;
                int fc = PaperdollOrder.ApplyDirectionCloak(full, PaperdollOrder.Filter(raw, includeBackpack: false, full), dir);
                int hn = 0;

                for (int i = 0; i < fc && hn < n; i++)
                {
                    if (slotLayers.Slice(0, n).IndexOf((byte)full[i]) >= 0)
                    {
                        hard[hn++] = full[i];
                    }
                }

                int gn = AnimationRules.PaintOrder(in eq, dir, order);

                for (int i = 0; i < gn; i++)
                {
                    got[i] = (Layer)eq.Slots[order[i]].Layer;
                }

                CompareOrder($"ecs slots dir {dir}", serial, hard.Slice(0, hn), got.Slice(0, gn));
            }
        }

        /// <summary>The paperdoll: its order key (held-item preview included) and every layer's IsCovered.</summary>
        public static void CheckPaperdoll(Mobile mobile, ReadOnlySpan<ushort> key, bool altTorso, bool gargoyleBody)
        {
            LayerTables t = default;
            t.Gather(mobile);
            LayerSubject subject = Subject(mobile.Graphic, mobile.IsFemale, gargoyleBody);

            Span<Layer> raw = stackalloc Layer[PaperdollOrder.N];
            Span<Layer> hard = stackalloc Layer[PaperdollOrder.N];
            Span<Layer> data = stackalloc Layer[PaperdollOrder.N];
            PaperdollOrder.Build(key, altTorso, raw);
            int hc = PaperdollOrder.Filter(raw, includeBackpack: false, hard);
            int dc = BuildPaperdoll(key, subject, data);
            CompareOrder("paperdoll", mobile.Serial, hard.Slice(0, hc), data.Slice(0, dc));

            CheckCovered("paperdoll", mobile, LayerRuleSet.Paperdoll, ref t, subject);
        }

        private static void CheckCovered(string where, Mobile mobile, LayerRuleSet set, ref LayerTables t, LayerSubject subject)
        {
            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                CompareCovered(where, mobile.Serial, (Layer)l, Mobile.IsCovered(mobile, (Layer)l), IsCovered(set, (Layer)l, ref t, subject), ref t);
            }
        }

        /// <summary>MobileAnimationSystem's rebuild: its raw order (PaperdollOrder.Build), AnimationRules.IsCovered, FixGargoyleEquipment.</summary>
        public static void CheckEcs(uint serial, ReadOnlySpan<ushort> key, ReadOnlySpan<bool> present, ReadOnlySpan<ushort> gfx, ReadOnlySpan<ushort> anim, ushort body, bool female, bool gargoyle)
        {
            LayerSubject subject = Subject(body, female, gargoyle);
            Span<Layer> hard = stackalloc Layer[PaperdollOrder.N];
            Span<byte> data = stackalloc byte[PaperdollOrder.N];
            PaperdollOrder.Build(key, female || gargoyle, hard);
            Rules.BuildOrder(LayerRuleSet.World, key, subject, data);
            CompareOrder("ecs", serial, hard, MemoryMarshal.Cast<byte, Layer>(data));

            LayerTables t = default;

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                t.Present[l] = present[l];
                t.Graphic[l] = present[l] ? gfx[l] : (ushort)0;
                t.Anim[l] = present[l] ? anim[l] : (ushort)0;
            }

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                CompareCovered("ecs", serial, (Layer)l, AnimationRules.IsCovered((Layer)l, gfx, anim, present, body), IsCovered(LayerRuleSet.World, (Layer)l, ref t, subject), ref t);

                if (present[l] && anim[l] != 0)
                {
                    ushort h = gargoyle ? AnimationRules.FixGargoyleEquipment(anim[l]) : anim[l], d = Remap(anim[l], subject);
                    Checks++;

                    if (h != d)
                    {
                        Report($"[layers] mismatch (ecs remap) 0x{serial:X8} anim 0x{anim[l]:X4}: hard 0x{h:X4} | data 0x{d:X4}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// T3b, ecs_layer_rules: the ECS slots' paint order per facing from the data's direction rules. MobileAnimationSystem's
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
}
