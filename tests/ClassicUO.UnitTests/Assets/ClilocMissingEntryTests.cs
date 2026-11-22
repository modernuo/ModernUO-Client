using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // Upstream 778c6e4aa (a missing cliloc is a "MegaCliloc: missing" placeholder, never null) is in our ClilocLoader. The
    // callers that skipped a missing string by checking for null or empty lost the skip: the placeholder, with its own
    // ~1_val~ ~2_val~, landed inside a sentence (a "#id" argument) or inside 0xBF.10's attribute list. Both now ask
    // TryGetString and leave the missing string out.
    public class ClilocMissingEntryTests : IDisposable
    {
        private const int MISSING = 1_099_999;

        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "cuo-clilocmiss-" + Guid.NewGuid().ToString("N"));

        public ClilocMissingEntryTests() => Directory.CreateDirectory(_tmp);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tmp, true);
            }
            catch (IOException)
            {
            }
        }

        private ClilocLoader Loaded(params (int Id, string Text)[] entries)
        {
            byte[] payload = ClilocTable.Build(entries.Select(e => (e.Id, Encoding.UTF8.GetBytes(e.Text))).ToList());
            var w = new RecordStoreWriter(1);
            var (m0, m1) = ClilocTable.PackLanguage("enu");
            var stored = RecordStoreWriter.Encode(payload, StoreConst.CodecRaw);
            w.Slots[0] = new StoreSlot { Record = (uint)w.Add(stored, (uint)payload.Length, StoreConst.CodecRaw, m0, m1, ClilocStore.KindLanguage) + 1 };
            string path = Path.Combine(_tmp, "cliloc.bin");
            w.Write(path, TextConst.MagicCliloc, 0);

            var fm = new UOFileManager(ClientVersion.CV_7090, _tmp, null);
            ClilocLoader c = fm.Clilocs;
            c.PackFile = path;
            c.Load("enu");

            return c;
        }

        [Fact]
        public void A_Missing_Cliloc_Is_A_Placeholder_Not_Null()
        {
            ClilocLoader c = Loaded((1000, "known"));

            c.GetString(MISSING).Should().StartWith($"MegaCliloc: missing {MISSING}");
            c.GetString(MISSING, "fallback").Should().Be("fallback");
            c.GetString(MISSING, true).Should().Be("", "the camel-case overload's fallback is empty");
            c.Translate(MISSING, "a\tb").Should().Be($"MegaCliloc: missing {MISSING} [a] [b]");
            c.TryGetString(MISSING, out string none).Should().BeFalse();
            none.Should().BeNull();
        }

        [Fact]
        public void A_Missing_Hash_Argument_Is_Left_Empty_Not_The_Placeholder()
        {
            ClilocLoader c = Loaded((1000, "You see ~1_NAME~ and ~2_THING~."), (1001, "a sword"));

            c.Translate(1000, "#1001\tsomething").Should().Be("You see a sword and something.");
            c.Translate(1000, $"#{MISSING}\tsomething").Should().Be("You see  and something.");
        }

        private static byte[] EquipInfo(params uint[] fields)
        {
            // (next, charges) as the packet has them: the first next, then charges, then (next, charges) pairs
            var bytes = new List<byte>();
            var buf = new byte[4];

            for (int i = 0; i < fields.Length; i++)
            {
                if (i % 2 == 0)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buf, fields[i]);
                    bytes.AddRange(buf);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)fields[i]);
                    bytes.Add(buf[0]);
                    bytes.Add(buf[1]);
                }
            }

            return bytes.ToArray();
        }

        private static string EquipInfoText(ClilocLoader c, byte[] data)
        {
            var p = new NetReader(data);

            return PacketHandlers.EquipInfoText(ref p, c);
        }

        [Fact]
        public void Equip_Info_Lists_The_Known_Attributes_And_Leaves_Out_A_Missing_One()
        {
            ClilocLoader c = Loaded((1000, "blessed"), (1001, "cursed"), (1002, "charges"));
            const uint NONE = 0xFFFF; // charges -1

            EquipInfoText(c, EquipInfo(1000, NONE, 1001, NONE)).Should().Be(" [blessed/cursed]");

            // a missing first attribute: the list still opens with " [" (the skip is not counted)
            EquipInfoText(c, EquipInfo(MISSING, NONE, 1000, NONE, 1001, NONE)).Should().Be(" [blessed/cursed]");
            EquipInfoText(c, EquipInfo(1000, NONE, MISSING, NONE, 1001, NONE)).Should().Be(" [blessed/cursed]");
            EquipInfoText(c, EquipInfo(1002, 5, MISSING, 7)).Should().Be("\n[charges : 5]");
            EquipInfoText(c, EquipInfo(MISSING, NONE)).Should().BeNull("nothing to show");
        }
    }
}
