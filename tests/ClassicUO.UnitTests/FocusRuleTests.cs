using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests
{
    // FocusRule: automation (bench, driver, CUO_IGNORE_FOCUS) counts as focused and not minimized; the simulated states force the
    // throttled paths; a normal session follows the real window.
    public class FocusRuleTests
    {
        [Theory]
        //     windowActive automation forced  inactive
        [InlineData(true,  false, false, false)]
        [InlineData(false, false, false, true)]   // a real unfocused player client
        [InlineData(false, true,  false, false)]  // an unfocused bench client counts as focused
        [InlineData(true,  true,  true,  true)]   // `inactive on` forces the throttle even in automation
        [InlineData(false, true,  true,  true)]
        [InlineData(true,  false, true,  true)]
        public void IsInactive(bool windowActive, bool automation, bool forced, bool expected)
        {
            FocusRule.IsInactive(windowActive, automation, forced).Should().Be(expected);
        }

        [Theory]
        //     real   automation forced  minimized
        [InlineData(false, false, false, false)]
        [InlineData(true,  false, false, true)]   // a really minimized player client
        [InlineData(true,  true,  false, false)]  // a minimized bench client still draws
        [InlineData(false, true,  true,  true)]   // `minimized on` forces the skip in automation
        [InlineData(false, false, true,  true)]
        public void IsMinimized(bool real, bool automation, bool forced, bool expected)
        {
            FocusRule.IsMinimized(real, automation, forced).Should().Be(expected);
        }

#if CUO_DEVTOOLS
        [Fact]
        public void A_Driver_Session_Is_Automation()
        {
            bool before = FocusRule.DriverSession;

            try
            {
                FocusRule.DriverSession = true;
                FocusRule.Automation.Should().BeTrue();
            }
            finally
            {
                FocusRule.DriverSession = before;
            }
        }
#endif
    }
}
