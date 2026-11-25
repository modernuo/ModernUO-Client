using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection;
using Xunit;

namespace ClassicUO.UnitTests.Diagnostics;

public class DiagnosticHookTests
{
    [Fact]
    public void Conditional_Hook_Drops_Call_And_Argument_Evaluation_Without_Devtools()
    {
        const string source = """
            class Hook
            {
                [System.Diagnostics.Conditional("CUO_DEVTOOLS")]
                public static void Report(string detail) { }
            }
            public class Caller
            {
                public static int Evaluations;
                static string Evaluate() { Evaluations++; return "detail"; }
                public static void Run() => Hook.Report(Evaluate());
            }
            """;
        foreach (bool devtools in new[] { false, true })
        {
            var bytes = ReleaseDiagnosticsTests.Compile(source, devtools ? new[] { "CUO_DEVTOOLS" } : Array.Empty<string>());
            var assembly = Assembly.Load(bytes);
            var caller = assembly.GetType("Caller");
            caller.GetMethod("Run").Invoke(null, null);
            Assert.Equal(devtools ? 1 : 0, caller.GetField("Evaluations").GetValue(null));
        }
    }

#if CUO_DEVTOOLS
    [Fact]
    public void Shared_Helper_Is_Conditional_And_Absent_From_Nodev()
    {
        var method = typeof(ClassicUO.DevTools.Diag).GetMethod("Report");
        var attribute = method.GetCustomAttribute<System.Diagnostics.ConditionalAttribute>();
        Assert.Equal("CUO_DEVTOOLS", attribute?.ConditionString);
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "release-guard", "cuo.dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        Assert.DoesNotContain(metadata.TypeDefinitions, handle =>
        {
            var type = metadata.GetTypeDefinition(handle);
            return metadata.GetString(type.Namespace) == "ClassicUO.DevTools" && metadata.GetString(type.Name) == "Diag";
        });
    }
#endif
}
