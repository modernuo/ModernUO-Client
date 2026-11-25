using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ClassicUO.UnitTests.Diagnostics;

public class ReleaseDiagnosticsTests
{
    private static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        if (!type.GetDeclaringType().IsNil)
            return TypeName(metadata, type.GetDeclaringType()) + "+" + name;
        string ns = metadata.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    // These are production validation results / gameplay data, not diagnostic instrumentation.
    // Exact full names keep this distinction from exempting an unrelated future checker.
    private static readonly HashSet<string> ProductionTypes = new(StringComparer.Ordinal)
    {
        "ClassicUO.Assets.SoundsLoader+RecordCheck",
        "ClassicUO.Assets.SoundsLoader+SlotCheck",
        "ClassicUO.DeviceProbe",
        "ClassicUO.Game.WorldWriter+AttachCheck",
        "ClassicUO.Game.Map.Map+ArtCheck",
        "ClassicUO.Game.Scenes.GpuItemGen+TileCheck",
        "ClassicUO.Game.UI.Gumps.StatusGumpOld+MobileStats",
        "ClassicUO.Game.UI.Gumps.StatusGumpModern+MobileStats"
    };

    internal static List<string> Scan(byte[] assembly)
    {
        using var stream = new MemoryStream(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var leaks = new List<string>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            string fullName = TypeName(metadata, handle);
            var names = fullName.Split('.', '+').Select(part => part.Split('`')[0]);
            if (names.Any(name =>
                Regex.IsMatch(name, @"(Check$|Checker$|^Diag|Diagnostics|Stats$|Probe$|Audit$)") ||
                name is "DevBench" or "MemLog" or "GoldenCapture" or "LightFrameLog" or "NetTrace" or
                    "Harness" or "EcsSessionDriver" or "AnimFreeze" or
                    "FramePhase" or "FrameCounter" or "FunnelField" or "SlowFrameKind" or "LatencyHistogram"))
            {
                if (!ProductionTypes.Contains(fullName)) leaks.Add($"type {fullName}");
            }
        }

        // Scan all user strings, including unreachable code and static initializers. This is deliberately
        // stronger than looking only for direct Environment.GetEnvironmentVariable calls.
        int offset = 1;
        int size = metadata.GetHeapSize(HeapIndex.UserString);
        while (offset < size)
        {
            var handle = MetadataTokens.UserStringHandle(offset);
            string value = metadata.GetUserString(handle);
            foreach (Match match in Regex.Matches(value, @"CUO_[A-Z0-9_]*_CHECK\b"))
                leaks.Add($"environment {match.Value}");
            var next = metadata.GetNextHandle(handle);
            if (next.IsNil) break;
            offset = MetadataTokens.GetHeapOffset(next);
        }
        return leaks.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Release_Client_Contains_No_Diagnostics()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "release-guard");
        foreach (string name in new[]
        {
            "cuo", "ClassicUO.Assets", "ClassicUO.Ecs", "ClassicUO.IO", "ClassicUO.Renderer",
            "ClassicUO.Utility", "Uo.Buffers", "Uo.Pack", "Uo.Render"
        })
            Assert.True(File.Exists(Path.Combine(directory, name + ".dll")), $"The normal test build must prepare nodev {name}.");
        var leaks = Directory.GetFiles(directory, "*.dll")
            .SelectMany(path => Scan(File.ReadAllBytes(path)).Select(leak => $"{Path.GetFileName(path)}: {leak}"));
        var findings = leaks.ToArray();
        Assert.True(findings.Length == 0, string.Join(Environment.NewLine, findings));
    }

    [Theory]
    [InlineData("namespace Example.Diagnostics { class Innocent {} }", "type Example.Diagnostics.Innocent")]
    [InlineData("class FooCheck {}", "type FooCheck")]
    [InlineData("class FooCheck<T> {}", "type FooCheck`1")]
    [InlineData("namespace Example.Diagnostics { class Ordinary { class Nested {} } }", "type Example.Diagnostics.Ordinary+Nested")]
    [InlineData("class Outer { class NestedCheck {} }", "type Outer+NestedCheck")]
    [InlineData("class ParentCheck { class Payload {} }", "type ParentCheck+Payload")]
    [InlineData("enum FramePhase { Draw }", "type FramePhase")]
    [InlineData("namespace Example.Harness { class Replay {} }", "type Example.Harness.Replay")]
    [InlineData("class Holder { static string Read() => System.Environment.GetEnvironmentVariable(\"CUO_FAKE_CHECK\"); }", "environment CUO_FAKE_CHECK")]
    public void Scanner_Rejects_Diagnostic_Types_And_Environment_Strings(string source, string expected)
    {
        Assert.Contains(expected, Scan(Compile(source)));
    }

    [Theory]
    [InlineData("namespace ClassicUO { struct DeviceProbe {} }")]
    [InlineData("namespace ClassicUO.Game { class WorldWriter { enum AttachCheck { Ok } } }")]
    [InlineData("namespace ClassicUO.Game.Scenes { class GpuItemGen { enum TileCheck { Trust } } }")]
    public void Scanner_Accepts_Production_Validation_Types(string source)
    {
        Assert.Empty(Scan(Compile(source)));
    }

    [Fact]
    public void Scanner_Accepts_Ordinary_Production_Code()
    {
        Assert.Empty(Scan(Compile("class Ordinary { static string Read() => System.Environment.GetEnvironmentVariable(\"CUO_PACK_ROOTS\"); }")));
    }

    internal static byte[] Compile(string source, params string[] symbols)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: symbols));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GuardFixture", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }
}
