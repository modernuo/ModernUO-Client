using System;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests
{
    public class RendererSelectionTests
    {
        private static readonly DeviceProbe ProbeOk = new(true, null);
        private static readonly DeviceProbe ProbeFail = new(false, "no vulkan");

        private static Func<DeviceProbe> Probe(DeviceProbe result, Action called = null) => () =>
        {
            called?.Invoke();

            return result;
        };

        private static string D3D11() => "D3D11";

        [Theory]
        [InlineData(null, null, "Auto", "default")]
        [InlineData("", null, "Auto", "default")]
        [InlineData("auto", null, "Auto", "settings.json")]
        [InlineData("gpu", null, "Gpu", "settings.json")]
        [InlineData("Legacy", null, "Legacy", "settings.json")]
        [InlineData("legacy", "gpu", "Gpu", "-worldrenderer")]
        [InlineData("gpu", "legacy", "Legacy", "-worldrenderer")]
        [InlineData("gpu", "AUTO", "Auto", "-worldrenderer")]
        [InlineData("legacy", " ", "Legacy", "settings.json")]
        [InlineData("compatibility", null, "Legacy", "settings.json")]
        [InlineData("auto", "Compatibility", "Legacy", "-worldrenderer")]
        public void ParseMode_CommandLineOverridesSetting(string setting, string arg, string expected, string source)
        {
            RendererSelection.ParseMode(setting, arg, out string actualSource, out string warning).ToString().Should().Be(expected);
            actualSource.Should().Be(source);
            warning.Should().BeNull();
        }

        [Theory]
        [InlineData("legacy", "Legacy")]
        [InlineData("Compatibility", "Legacy")]
        [InlineData("auto", "Auto")]
        [InlineData("gpu", "Gpu")]
        public void ParseMode_AcceptsEachSettingValue(string setting, string expected)
        {
            RendererSelection.ParseMode(setting, null, out _, out string warning).ToString().Should().Be(expected);
            warning.Should().BeNull();
        }

        [Theory]
        [InlineData("Auto")]
        [InlineData("Gpu")]
        [InlineData("Legacy")]
        public void SettingValue_RoundTripsThroughParseMode(string modeName)
        {
            var mode = System.Enum.Parse<WorldRendererMode>(modeName);
            RendererSelection.ParseMode(RendererSelection.SettingValue(mode), null, out _, out _).Should().Be(mode);
        }

        [Fact]
        public void ParseMode_UnknownValue_IsAutoWithWarning()
        {
            RendererSelection.ParseMode("vulkan", null, out _, out string warning).Should().Be(WorldRendererMode.Auto);
            warning.Should().Contain("vulkan");
        }

        [Fact]
        public void ForcedLegacy_NeverProbes_AndKeepsTheDefaultDevice()
        {
            bool probed = false;
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Legacy, null, Probe(ProbeOk, () => probed = true), D3D11);

            probed.Should().BeFalse();
            pre.GpuCandidate.Should().BeFalse();
            pre.Fna3dDriver.Should().BeNull();
            RendererSelection.AfterDevice(pre, "vulkan", () => throw new InvalidOperationException()).Outcome.Should().Be(RendererOutcome.Legacy);
        }

        [Fact]
        public void Auto_ProbeOk_WorldOk_IsGpu()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, null, Probe(ProbeOk), D3D11);

            pre.GpuCandidate.Should().BeTrue();
            pre.Fna3dDriver.Should().BeNull();
            RendererSelection.AfterDevice(pre, "vulkan", () => null).Should().Be(new PostDeviceDecision(RendererOutcome.Gpu, null));
        }

        [Fact]
        public void Auto_DeviceProbeFails_IsLegacyOnTheLegacyDriver()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, null, Probe(ProbeFail), D3D11);

            pre.GpuCandidate.Should().BeFalse();
            pre.Fna3dDriver.Should().Be("D3D11");
            pre.Reason.Should().Be("no vulkan");

            var post = RendererSelection.AfterDevice(pre, null, () => throw new InvalidOperationException());
            post.Should().Be(new PostDeviceDecision(RendererOutcome.Legacy, "no vulkan"));
        }

        [Fact]
        public void Auto_WorldProbeFails_IsLegacyOnTheSdlGpuDevice()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, null, Probe(ProbeOk), D3D11);

            RendererSelection.AfterDevice(pre, "vulkan", () => "pack missing").Should().Be(new PostDeviceDecision(RendererOutcome.Legacy, "pack missing"));
        }

        [Fact]
        public void Auto_Fna3dLandsOffSdlGpu_IsLegacy()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, null, Probe(ProbeOk), D3D11);
            bool worldCalled = false;

            var post = RendererSelection.AfterDevice(pre, null, () => { worldCalled = true; return null; });

            post.Outcome.Should().Be(RendererOutcome.Legacy);
            worldCalled.Should().BeFalse();
        }

        [Fact]
        public void SdlGpuOnAnotherBackend_IsNotGpu()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, null, Probe(ProbeOk), D3D11);

            RendererSelection.AfterDevice(pre, "direct3d12", () => null).Outcome.Should().Be(RendererOutcome.Legacy);
        }

        [Theory]
        [InlineData("OpenGL")]
        [InlineData("D3D11")]
        public void Auto_UserForcedNonSdlGpuDriver_IsLegacyWithoutProbe(string driver)
        {
            bool probed = false;
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, driver, Probe(ProbeOk, () => probed = true), D3D11);

            probed.Should().BeFalse();
            pre.Fna3dDriver.Should().BeNull("the user's driver stays");
            pre.Reason.Should().Contain(driver);
            RendererSelection.AfterDevice(pre, null, () => null).Outcome.Should().Be(RendererOutcome.Legacy);
        }

        [Fact]
        public void Auto_UserForcedVulkan_ProbeFails_KeepsTheUsersDriver()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, "Vulkan", Probe(ProbeFail), D3D11);

            pre.Fna3dDriver.Should().BeNull();
            pre.GpuCandidate.Should().BeFalse();
        }

        [Fact]
        public void Auto_UserForcedVulkan_ProbeOk_IsGpu()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, "Vulkan", Probe(ProbeOk), D3D11);

            RendererSelection.AfterDevice(pre, "vulkan", () => null).Outcome.Should().Be(RendererOutcome.Gpu);
        }

        [Fact]
        public void ForcedGpu_ProbeOk_WorldOk_IsGpu()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Gpu, null, Probe(ProbeOk), D3D11);

            RendererSelection.AfterDevice(pre, "vulkan", () => null).Outcome.Should().Be(RendererOutcome.Gpu);
        }

        [Fact]
        public void ForcedGpu_DeviceProbeFails_FailsAfterTheDevice_WithoutForcingALegacyDriver()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Gpu, null, Probe(ProbeFail), D3D11);

            pre.Fna3dDriver.Should().BeNull();
            // even if FNA3D then gets an SDL_GPU device (a simulated failure), the probe's answer stands
            RendererSelection.AfterDevice(pre, "vulkan", () => null).Should().Be(new PostDeviceDecision(RendererOutcome.Fail, "no vulkan"));
        }

        [Fact]
        public void ForcedGpu_WorldFails_OrNoSdlGpu_Fails()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Gpu, null, Probe(ProbeOk), D3D11);

            RendererSelection.AfterDevice(pre, "vulkan", () => "boom").Should().Be(new PostDeviceDecision(RendererOutcome.Fail, "boom"));
            RendererSelection.AfterDevice(pre, null, () => null).Outcome.Should().Be(RendererOutcome.Fail);
        }

        [Fact]
        public void Auto_NoSpirvShaders_IsLegacyOnTheLegacyDriver_WithoutProbe()
        {
            bool probed = false;
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, null, Probe(ProbeOk, () => probed = true), D3D11, spirvShaders: false);

            probed.Should().BeFalse();
            pre.GpuCandidate.Should().BeFalse();
            pre.Fna3dDriver.Should().Be("D3D11");
            pre.Reason.Should().Contain("SPIR-V");

            var post = RendererSelection.AfterDevice(pre, "vulkan", () => throw new InvalidOperationException());
            post.Outcome.Should().Be(RendererOutcome.Legacy);
            post.Reason.Should().Contain("SPIR-V");
        }

        [Fact]
        public void Auto_NoSpirvShaders_UserForcedVulkan_KeepsTheUsersDriver_IsLegacy()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Auto, "Vulkan", Probe(ProbeOk), D3D11, spirvShaders: false);

            pre.Fna3dDriver.Should().BeNull();
            RendererSelection.AfterDevice(pre, "vulkan", () => null).Outcome.Should().Be(RendererOutcome.Legacy);
        }

        [Fact]
        public void ForcedGpu_NoSpirvShaders_FailsWithTheReason()
        {
            bool probed = false;
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Gpu, null, Probe(ProbeOk, () => probed = true), D3D11, spirvShaders: false);

            probed.Should().BeFalse();
            pre.Fna3dDriver.Should().BeNull();

            var post = RendererSelection.AfterDevice(pre, "vulkan", () => null);
            post.Outcome.Should().Be(RendererOutcome.Fail);
            post.Reason.Should().Contain("SPIR-V");
        }

        [Fact]
        public void ForcedLegacy_NoSpirvShaders_IsLegacy()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Legacy, null, Probe(ProbeOk), D3D11, spirvShaders: false);

            pre.Fna3dDriver.Should().BeNull();
            RendererSelection.AfterDevice(pre, "vulkan", () => null).Should().Be(new PostDeviceDecision(RendererOutcome.Legacy, "forced"));
        }

        [Fact]
        public void ForcedGpu_WithForcedOpenGL_Fails()
        {
            var pre = RendererSelection.BeforeDevice(WorldRendererMode.Gpu, "OpenGL", Probe(ProbeOk), D3D11);

            RendererSelection.AfterDevice(pre, null, () => null).Outcome.Should().Be(RendererOutcome.Fail);
        }

        [Theory]
        [InlineData("Vulkan", true)]
        [InlineData("sdlgpu", true)]
        [InlineData("D3D12", true)]
        [InlineData("D3D11", false)]
        [InlineData("OpenGL", false)]
        [InlineData(null, false)]
        public void IsSdlGpuDriver(string driver, bool expected)
        {
            RendererSelection.IsSdlGpuDriver(driver).Should().Be(expected);
        }
    }
}
