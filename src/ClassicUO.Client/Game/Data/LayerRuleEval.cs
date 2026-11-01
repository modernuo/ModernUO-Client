using System;
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

        public static LayerSubject Subject(ushort body, bool female, bool gargoyleDraw) =>
            new LayerSubject(Rules.GroupOf(body), (byte)((female ? LayerRulesConst.FlagFemale : 0) | (gargoyleDraw ? LayerRulesConst.FlagGargoyleDraw : 0)));

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

                if (gargoyleDraw && anim != 0)
                {
                    ushort h = anim;
                    Mobile.FixGargoyleEquipmentsHard(ref h);
                    ushort d = Remap(anim, subject);
                    Checks++;

                    if (h != d)
                    {
                        Report($"[layers] mismatch (world remap) 0x{mobile.Serial:X8} anim 0x{anim:X4}: hard 0x{h:X4} | data 0x{d:X4}");
                    }
                }
            }
        }

        /// <summary>MobileView's pick: one worn layer's IsCovered and the gargoyle remap.</summary>
        public static void CheckPick(Mobile mobile, Layer layer, ref LayerTables t, LayerSubject subject)
        {
            CompareCovered("pick", mobile.Serial, layer, Mobile.IsCovered(mobile, layer), IsCovered(LayerRuleSet.World, layer, ref t, subject), ref t);
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

                if (gargoyle && present[l] && anim[l] != 0)
                {
                    ushort h = AnimationRules.FixGargoyleEquipment(anim[l]), d = Remap(anim[l], subject);
                    Checks++;

                    if (h != d)
                    {
                        Report($"[layers] mismatch (ecs remap) 0x{serial:X8} anim 0x{anim[l]:X4}: hard 0x{h:X4} | data 0x{d:X4}");
                    }
                }
            }
        }
    }
}
