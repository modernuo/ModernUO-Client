using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 S8a: [mobile-no-record] logs each serial once (bounded), so record-less mobiles drawn every frame, alternating or
    // not, do not log or allocate per frame
    public class MobileNoRecordLogTests
    {
        [Fact]
        public void Each_Serial_Logs_Once_And_The_Set_Is_Bounded()
        {
            LegacySpriteSink.ResetNoRecordLog();

            try
            {
                LegacySpriteSink.FirstNoRecord(0x100).Should().BeTrue();
                LegacySpriteSink.FirstNoRecord(0x200).Should().BeTrue();

                for (int frame = 0; frame < 50; frame++)
                {
                    LegacySpriteSink.FirstNoRecord(0x100).Should().BeFalse("alternating serials were logged already");
                    LegacySpriteSink.FirstNoRecord(0x200).Should().BeFalse();
                }

                int logged = 2;

                for (uint s = 0x1000; s < 0x1000 + 200; s++)
                {
                    if (LegacySpriteSink.FirstNoRecord(s))
                    {
                        logged++;
                    }
                }

                logged.Should().Be(64, "at most 64 serials are logged");
            }
            finally
            {
                LegacySpriteSink.ResetNoRecordLog();
            }
        }
    }
}
