using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    /// <summary>
    /// Uo.Render embeds committed shader binaries (poc/Uo.Render/Shaders/compiled), so a build needs no dxc. These tests catch
    /// a shader source edited without regenerating the binaries, and a build that lost a binary.
    /// </summary>
    public class ShaderBundleTests
    {
        private const string Regenerate = "dotnet build poc/Uo.Render -p:CompileShaders=true (docs/dev/runbook.md, Shader toolchain)";

        [Fact]
        public void CompiledShaders_MatchTheirSource()
        {
            string shaders = Path.Combine(WriteSiteScanner.FindRepoRoot(), "poc", "Uo.Render", "Shaders");
            string[] sources = Directory.GetFiles(shaders, "*.hlsl");

            sources.Should().NotBeEmpty();

            foreach (string source in sources)
            {
                string name = Path.GetFileName(source);
                string hashFile = Path.Combine(shaders, "compiled", name + ".sha256");

                File.Exists(hashFile).Should().BeTrue($"{name} has no compiled shaders; regenerate with {Regenerate}");

                // the same hash the regenerate target writes: sha256 of the UTF-8 text with CR removed (CRLF and LF checkouts agree)
                string text = File.ReadAllText(source).Replace("\r", "");
                string actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                string stored = File.ReadAllText(hashFile).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0];

                stored.Should().Be(actual, $"Shaders/{name} changed since its compiled shaders were made; regenerate with {Regenerate} and commit Shaders/compiled");
            }
        }

        [Fact]
        public void UoRender_EmbedsDxilAndSpirv()
        {
            string[] names = typeof(global::Uo.Render.Renderer).Assembly.GetManifestResourceNames();

            names.Should().Contain(new[] { "sprite_vs.dxil", "sprite_ps.dxil", "sprite_vs.spv", "sprite_ps.spv" });

            // every variant in both formats: D3D12 takes DXIL, Vulkan (the client's Gpu path) takes SPIR-V
            var dxil = names.Where(n => n.EndsWith(".dxil")).Select(Path.GetFileNameWithoutExtension).OrderBy(n => n);
            var spirv = names.Where(n => n.EndsWith(".spv")).Select(Path.GetFileNameWithoutExtension).OrderBy(n => n);
            spirv.Should().Equal(dxil);

            global::Uo.Render.Renderer.SpirvShadersEmbedded.Should().BeTrue();
        }
    }
}
