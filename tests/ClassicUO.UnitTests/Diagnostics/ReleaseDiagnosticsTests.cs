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
using Mono.Cecil;
using Mono.Cecil.Cil;

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
                if (!ProductionTypes.Contains(fullName)) leaks.Add($"type|{fullName}");
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
                leaks.Add($"environment|{match.Value}");
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
        foreach (string name in AssemblyNames)
            Assert.True(File.Exists(Path.Combine(directory, name + ".dll")), $"The normal test build must prepare nodev {name}.");
        var paths = Directory.GetFiles(directory, "*.dll")
            .Where(path => IsFirstPartyAssembly(Path.GetFileName(path))).ToArray();
        // Independent of the debt ratchet and regeneration: retained calls always fail.
        Assert.Empty(ScanConditionalCalls(paths));
        var actual = paths.SelectMany(path => Scan(File.ReadAllBytes(path))
            .Select(leak => $"{Path.GetFileName(path)}|{leak}"))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        string root = Ecs.WriteSiteScanner.FindRepoRoot();
        string allowlist = Path.Combine(root, "tests", "ClassicUO.UnitTests", "Diagnostics", "ReleaseDiagnosticsAllowlist.txt");
        if (Environment.GetEnvironmentVariable("CUO_RELEASE_DIAGNOSTICS_REGEN") == "1")
        {
            File.WriteAllLines(allowlist, actual);
            return;
        }
        var allowed = ParseAllowlist(File.ReadAllLines(allowlist));
        AssertRatchet(actual, allowed);
    }

    private static readonly string[] AssemblyNames =
    {
        "cuo", "ClassicUO.Assets", "ClassicUO.Ecs", "ClassicUO.IO", "ClassicUO.Renderer",
        "ClassicUO.Utility", "Uo.Buffers", "Uo.Pack", "Uo.Render"
    };

    private static bool IsFirstPartyAssembly(string name) =>
        Regex.IsMatch(name, @"^(cuo|ClassicUO\.[A-Za-z0-9_.]+|Uo\.[A-Za-z0-9_.]+)\.dll$");

    internal static HashSet<string> ParseAllowlist(IEnumerable<string> rows)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string previous = null;
        foreach (string row in rows)
        {
            string[] parts = row.Split('|');
            Assert.True(parts.Length == 3 && IsFirstPartyAssembly(parts[0]) &&
                (parts[1] == "type" && Regex.IsMatch(parts[2], @"^[^\s|]+$") ||
                 parts[1] == "environment" && Regex.IsMatch(parts[2], @"^CUO_[A-Z0-9_]*_CHECK$")),
                "Malformed release diagnostics row: " + row);
            Assert.True(result.Add(row), "Duplicate release diagnostics row: " + row);
            Assert.True(previous == null || StringComparer.Ordinal.Compare(previous, row) < 0,
                "Release diagnostics rows must be sorted: " + row);
            previous = row;
        }
        return result;
    }

    internal static void AssertRatchet(IEnumerable<string> actual, IEnumerable<string> allowed)
    {
        var added = actual.Except(allowed, StringComparer.Ordinal);
        var removed = allowed.Except(actual, StringComparer.Ordinal);
        Assert.True(!added.Any() && !removed.Any(),
            "New diagnostics:\n" + string.Join("\n", added) + "\nStale debt (regenerate):\n" + string.Join("\n", removed));
    }

    internal static List<string> ScanConditionalCalls(params string[] paths)
    {
        using var resolver = new DefaultAssemblyResolver();
        foreach (string directory in paths.Select(Path.GetDirectoryName).Distinct())
            resolver.AddSearchDirectory(directory);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));
        var findings = new List<string>();
        foreach (string path in paths)
        {
            using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
            foreach (var type in AllTypes(assembly.MainModule.Types))
            foreach (var method in type.Methods.Where(method => method.HasBody))
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj)) continue;
                var reference = (MethodReference)instruction.Operand;
                // CLR array pseudo-methods have no MethodDefinition and cannot carry attributes.
                if (reference.DeclaringType is ArrayType && reference.Name is ".ctor" or "Get" or "Set" or "Address")
                    continue;
                var target = reference.Resolve();
                Assert.True(target != null, "Cannot resolve call target: " + reference.FullName);
                if (target.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "System.Diagnostics.ConditionalAttribute" &&
                    attribute.ConstructorArguments.Count == 1 &&
                    attribute.ConstructorArguments[0].Value is "CUO_DEVTOOLS"))
                    findings.Add($"{Path.GetFileName(path)}: {method.FullName} -> {target.FullName}");
            }
        }
        return findings;
    }

    private static IEnumerable<Mono.Cecil.TypeDefinition> AllTypes(IEnumerable<Mono.Cecil.TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    [Theory]
    [InlineData("namespace Example.Diagnostics { class Innocent {} }", "type|Example.Diagnostics.Innocent")]
    [InlineData("class FooCheck {}", "type|FooCheck")]
    [InlineData("class FooCheck<T> {}", "type|FooCheck`1")]
    [InlineData("namespace Example.Diagnostics { class Ordinary { class Nested {} } }", "type|Example.Diagnostics.Ordinary+Nested")]
    [InlineData("class Outer { class NestedCheck {} }", "type|Outer+NestedCheck")]
    [InlineData("class ParentCheck { class Payload {} }", "type|ParentCheck+Payload")]
    [InlineData("enum FramePhase { Draw }", "type|FramePhase")]
    [InlineData("namespace Example.Harness { class Replay {} }", "type|Example.Harness.Replay")]
    [InlineData("class Holder { static string Helper(string name) => System.Environment.GetEnvironmentVariable(name); static string Read() => Helper(\"CUO_HELPER_CHECK\"); }", "environment|CUO_HELPER_CHECK")]
    [InlineData("class Holder { const string Name = \"CUO_CONST_CHECK\"; static string Read() => System.Environment.GetEnvironmentVariable(Name); }", "environment|CUO_CONST_CHECK")]
    [InlineData("class Holder { public static string Help() => \"Enable CUO_HELP_CHECK to compare\"; }", "environment|CUO_HELP_CHECK")]
    [InlineData("class Holder { static string Read() => System.Environment.GetEnvironmentVariable(\"CUO_FAKE_CHECK\"); }", "environment|CUO_FAKE_CHECK")]
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

    [Fact]
    public void Runtime_Composed_Controls_Are_Outside_The_Literal_Heuristic()
    {
        Assert.Empty(Scan(Compile("class Holder { public static string Read(string suffix) => System.Environment.GetEnvironmentVariable(\"CUO_\" + suffix + \"_CHECK\"); }")));
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
