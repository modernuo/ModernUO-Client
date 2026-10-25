using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // B8, the UI render cache: a kept UI target is reused only when the new recording equals the last one drawn.
    public class RecordedPassTests
    {
        private static readonly object TexA = new(), TexB = new();

        private static RecordedSegment Segment(int start, int count) => new()
        {
            Start = start,
            Count = count,
            Scissor = new Rectangle(0, 0, 1280, 720),
            Transform = Matrix.Identity,
            ViewportW = 1280,
            ViewportH = 720,
            Brightlight = float.NaN,
            CircleRadius = float.NaN,
            AfterBegin = true
        };

        private static UltimaBatcher2D.PositionNormalTextureColor4[] Vertices(int n)
        {
            var v = new UltimaBatcher2D.PositionNormalTextureColor4[n];

            for (int i = 0; i < n; i++)
            {
                v[i].Position0 = new Vector3(i * 10, 5, 0);
                v[i].Position1 = new Vector3(i * 10 + 8, 5, 0);
                v[i].Position2 = new Vector3(i * 10, 13, 0);
                v[i].Position3 = new Vector3(i * 10 + 8, 13, 0);
                v[i].Hue0 = v[i].Hue1 = v[i].Hue2 = v[i].Hue3 = new Vector3(0, 0, 1);
            }

            return v;
        }

        private static bool Equal(List<RecordedSegment> sa, List<RecordedSegment> sb, UltimaBatcher2D.PositionNormalTextureColor4[] va,
            UltimaBatcher2D.PositionNormalTextureColor4[] vb, object[] ta, object[] tb)
            => RecordedPass.RecordingsEqual<object>(sa, sb, va, vb, ta, tb);

        [Fact]
        public void IdenticalRecordings_AreEqual()
        {
            var s = new List<RecordedSegment> { Segment(0, 2), Segment(2, 1) };
            Equal(s, s.ToList(), Vertices(3), Vertices(3), new[] { TexA, TexA, TexB }, new[] { TexA, TexA, TexB }).Should().BeTrue();
        }

        [Fact]
        public void OneVertexValue_Differs()
        {
            var s = new List<RecordedSegment> { Segment(0, 3) };
            var b = Vertices(3);
            b[2].Hue3.Z = 0.5f; // the last float of the last sprite
            Equal(s, s, Vertices(3), b, new[] { TexA, TexA, TexA }, new[] { TexA, TexA, TexA }).Should().BeFalse();
        }

        [Fact]
        public void NegativeZero_CountsAsChanged()
        {
            var s = new List<RecordedSegment> { Segment(0, 1) };
            var a = Vertices(1);
            var b = Vertices(1);
            a[0].Normal0.X = 0f;
            b[0].Normal0.X = -0f;
            Equal(s, s, a, b, new[] { TexA }, new[] { TexA }).Should().BeFalse("bytes are compared: a needless redraw is safe");
        }

        [Fact]
        public void TextureReference_Differs()
        {
            var s = new List<RecordedSegment> { Segment(0, 2) };
            Equal(s, s, Vertices(2), Vertices(2), new[] { TexA, TexA }, new[] { TexA, TexB }).Should().BeFalse();
        }

        [Fact]
        public void SpriteCount_Differs()
        {
            var s = new List<RecordedSegment> { Segment(0, 2) };
            Equal(s, s, Vertices(2), Vertices(3), new[] { TexA, TexA }, new[] { TexA, TexA, TexA }).Should().BeFalse();
        }

        public static IEnumerable<object[]> StateChanges()
        {
            yield return new object[] { "split", (Func<RecordedSegment, RecordedSegment>)(s => { s.Count = 1; return s; }) };
            yield return new object[] { "blend", (Func<RecordedSegment, RecordedSegment>)(s => { s.Blend = Microsoft.Xna.Framework.Graphics.BlendState.Additive; return s; }) };
            yield return new object[] { "scissor on", (Func<RecordedSegment, RecordedSegment>)(s => { s.ScissorEnabled = true; return s; }) };
            yield return new object[] { "scissor rect", (Func<RecordedSegment, RecordedSegment>)(s => { s.Scissor = new Rectangle(10, 10, 100, 100); return s; }) };
            yield return new object[] { "transform", (Func<RecordedSegment, RecordedSegment>)(s => { s.Transform = Matrix.CreateScale(1.5f); return s; }) };
            yield return new object[] { "viewport", (Func<RecordedSegment, RecordedSegment>)(s => { s.ViewportW = 1920; return s; }) };
            yield return new object[] { "brightlight", (Func<RecordedSegment, RecordedSegment>)(s => { s.Brightlight = 1.5f; return s; }) };
            yield return new object[] { "transparency circle", (Func<RecordedSegment, RecordedSegment>)(s => { s.CircleRadius = 64; return s; }) };
            yield return new object[] { "effect apply", (Func<RecordedSegment, RecordedSegment>)(s => { s.AfterBegin = false; return s; }) };
        }

        [Theory]
        [MemberData(nameof(StateChanges))]
        public void SegmentState_Differs(string what, Func<RecordedSegment, RecordedSegment> change)
        {
            var a = new List<RecordedSegment> { Segment(0, 2) };
            var b = new List<RecordedSegment> { change(Segment(0, 2)) };
            Equal(a, b, Vertices(2), Vertices(2), new[] { TexA, TexA }, new[] { TexA, TexA }).Should().BeFalse(what);
        }

        [Fact]
        public void NewPass_IsInvalid()
        {
            var pass = new RecordedPass();
            pass.IsValid.Should().BeFalse("the first frame after creation always draws");
        }
    }

    // B8 compares recorded draws by texture reference, so every texture upload must bump TextureUploads (a texture changed in
    // place, like the minimap's atlas slot, would otherwise leave a stale cached UI).
    public class TextureUploadSitesTests
    {
        private static readonly Regex Upload = new(@"([A-Za-z_][A-Za-z0-9_\[\]\.]*)\??\.(SetData|SetDataPointerEXT)\s*[<(]", RegexOptions.Compiled);

        private static string FindSrc()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string src = Path.Combine(dir.FullName, "src", "ClassicUO.Renderer");

                if (Directory.Exists(src))
                {
                    return Path.Combine(dir.FullName, "src");
                }
            }

            return null;
        }

        [Fact]
        public void EveryTextureUpload_BumpsTheEpoch()
        {
            string src = FindSrc();
            src.Should().NotBeNull();
            var missing = new List<string>();
            int sites = 0;

            foreach (string project in new[] { "ClassicUO.Client", "ClassicUO.Renderer", "ClassicUO.Assets", "ClassicUO.Utility" })
            {
                string root = Path.Combine(src, project);

                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                        file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    {
                        continue;
                    }

                    string[] lines = File.ReadAllLines(file);

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].TrimStart();

                        if (line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("///"))
                        {
                            continue;
                        }

                        Match m = Upload.Match(lines[i]);

                        // vertex and index buffers aren't textures
                        if (!m.Success || m.Groups[1].Value.Contains("buffer", StringComparison.OrdinalIgnoreCase) ||
                            m.Groups[1].Value.EndsWith("Buffer", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        sites++;
                        bool bumped = false;

                        for (int j = i; j < Math.Min(lines.Length, i + 14); j++)
                        {
                            if (lines[j].Contains("TextureUploads.Changed()"))
                            {
                                bumped = true;

                                break;
                            }
                        }

                        if (!bumped)
                        {
                            missing.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {line}");
                        }
                    }
                }
            }

            sites.Should().BeGreaterThan(5, "the scan must find the known upload sites");
            missing.Should().BeEmpty();
        }
    }
}
