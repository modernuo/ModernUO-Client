using System;
using System.Collections.Generic;
using ClassicUO.Game.Data;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // CUO_LAYER_CACHE_CHECK (CUO_DEVTOOLS builds): a cached LayerDecision is compared with a fresh evaluation of the rules on
    // the wearer's tables. A current entry passes without allocating; a stale one (filled from other tables, its key kept)
    // is reported. LayerCacheCheck's state is static, so this runs with the other layering tests, alone.
    [Collection(nameof(LayerRulesStateCollection))]
    public class LayerCacheCheckTests
    {
        private const uint MOB = 0x0000_3000;

        private static LayerTables Tables(params (Layer layer, ushort graphic, ushort anim)[] worn)
        {
            LayerTables t = default;

            foreach ((Layer layer, ushort graphic, ushort anim) in worn)
            {
                t.Present[(int)layer] = true;
                t.Graphic[(int)layer] = graphic;
                t.Anim[(int)layer] = anim;
            }

            return t;
        }

        private static readonly (Layer, ushort, ushort)[] Dressed = { (Layer.Shirt, 0x1517, 0x1D2), (Layer.Pants, 0x152E, 0x1EB), (Layer.Robe, 0x1F03, 0x504) };

        [Fact]
        public void The_Every_Setting_Parses()
        {
#if CUO_DEVTOOLS
            LayerCacheCheck.ParseEvery(null).Should().Be(0);
            LayerCacheCheck.ParseEvery("0").Should().Be(0);
            LayerCacheCheck.ParseEvery("x").Should().Be(0);
            LayerCacheCheck.ParseEvery("1").Should().Be(1);
            LayerCacheCheck.ParseEvery("16").Should().Be(16);
#endif
        }

        [Fact]
        public void A_Current_Entry_Passes_And_A_Stale_One_Is_Reported()
        {
#if CUO_DEVTOOLS
            LayerRules rules = LayerRuleEval.Rules;
            LayerSubject subject = LayerRuleEval.Subject(0x190, false, false);
            LayerTables now = Tables(Dressed);
            var reports = new List<string>();
            LayerCacheCheck.MismatchObserver = reports.Add;

            try
            {
                var cached = new LayerDecision();
                cached.Reset(rules, subject);
                cached.Fill(ref now);
                long mismatches = LayerCacheCheck.Mismatches;

                LayerCacheCheck.Check(MOB, cached, ref now, rules, subject).Should().BeTrue();
                long before = GC.GetAllocatedBytesForCurrentThread();
                bool passed = LayerCacheCheck.Check(MOB, cached, ref now, rules, subject);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                passed.Should().BeTrue();
                allocated.Should().Be(0, "a passing check allocates nothing");
                reports.Should().BeEmpty();

                // stale: the answer for the wearer without the robe, under the same key
                LayerTables old = Tables(Dressed[0], Dressed[1]);
                cached.Fill(ref old);

                LayerCacheCheck.Check(MOB, cached, ref now, rules, subject).Should().BeFalse();
                reports.Should().ContainSingle().Which.Should().StartWith("[layers] cache mismatch 0x00003000");
                LayerCacheCheck.Mismatches.Should().Be(mismatches + 1);
            }
            finally
            {
                LayerCacheCheck.MismatchObserver = null;
            }
#endif
        }
    }
}
