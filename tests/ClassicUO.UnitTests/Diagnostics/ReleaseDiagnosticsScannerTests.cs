using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ClassicUO.UnitTests.Diagnostics;

public class ReleaseDiagnosticsScannerTests
{
    [Theory]
    [InlineData("cuo.dll|type|Outer+Nested`1", "cuo.dll|type|Other")]
    [InlineData("cuo.dll|environment|CUO_OLD_CHECK", "cuo.dll|environment|CUO_NEW_CHECK")]
    public void Ratchet_Rejects_Additions_And_Removals(string oldKey, string newKey)
    {
        Assert.ThrowsAny<Exception>(() => ReleaseDiagnosticsTests.AssertRatchet(new[] { oldKey, newKey }, new[] { oldKey }));
        Assert.ThrowsAny<Exception>(() => ReleaseDiagnosticsTests.AssertRatchet(new[] { oldKey }, new[] { oldKey, newKey }));
        ReleaseDiagnosticsTests.AssertRatchet(new[] { oldKey }, new[] { oldKey });
    }

    [Theory]
    [InlineData("cuo.dll|type|Foo\ncuo.dll|type|Foo")]
    [InlineData("cuo.dll:type Foo")]
    [InlineData("cuo.dll|call|Foo")]
    [InlineData("wrong.dll|type|Foo")]
    [InlineData("cuo.dll|type|")]
    [InlineData("cuo.dll|environment|CUO_CHECK extra")]
    [InlineData("cuo.dll|type|Foo|Bar")]
    [InlineData("cuo.dll|type|Z\ncuo.dll|type|A")]
    public void Ratchet_Rejects_Invalid_Rows(string rows)
    {
        Assert.ThrowsAny<Exception>(() => ReleaseDiagnosticsTests.ParseAllowlist(rows.Split('\n')));
    }

    [Fact]
    public void Ratchet_Preserves_Nested_Names_And_Generic_Arity()
    {
        const string key = "cuo.dll|type|Example.Outer`1+Nested`2";
        Assert.Contains(key, ReleaseDiagnosticsTests.ParseAllowlist(new[] { key }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Calls_Are_Checked_Independently_Of_Type_Debt_Including_Cross_Assembly(bool crossAssembly)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cuo-guard-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            const string hook = "public class Ordinary { [System.Diagnostics.Conditional(\"CUO_DEVTOOLS\")] public static void Observe<T>(T value) {} }";
            const string caller = "public class Caller { public static void Run() => Ordinary.Observe(42); }";
            string target = Path.Combine(directory, "Target.dll");
            File.WriteAllBytes(target, Compile("Target", hook));
            foreach (bool devtools in new[] { false, true })
            {
                string path = Path.Combine(directory, "Caller.dll");
                File.WriteAllBytes(path, Compile("Caller", (crossAssembly ? "" : hook) + caller, devtools,
                    crossAssembly ? target : null));
                var findings = ReleaseDiagnosticsTests.ScanConditionalCalls(path);
                Assert.Equal(devtools ? 1 : 0, findings.Count);
                if (devtools) Assert.Contains("Observe", findings.Single());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Runtime_Array_Methods_Are_Not_Conditional_Calls()
    {
        string path = Path.Combine(Path.GetTempPath(), "cuo-array-" + Guid.NewGuid() + ".dll");
        try
        {
            File.WriteAllBytes(path, Compile("ArrayFixture",
                "public class Arrays { public static int Run() { var a = new int[2,3]; a[1,2] = 4; return a[1,2]; } }"));
            Assert.Empty(ReleaseDiagnosticsTests.ScanConditionalCalls(path));
        }
        finally { File.Delete(path); }
    }

    private static byte[] Compile(string name, string source, bool devtools = false, string reference = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToList();
        if (reference != null) references.Add(MetadataReference.CreateFromFile(reference));
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(
            preprocessorSymbols: devtools ? new[] { "CUO_DEVTOOLS" } : Array.Empty<string>()));
        var compilation = CSharpCompilation.Create(name, new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }
}
