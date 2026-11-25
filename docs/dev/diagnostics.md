# Diagnostics and checkers

Checkers stay in the client source tree, close to the subsystem they observe. Test scenes, jobs, golden results and analysis can live in the private platform-tests repository; shipping code must not depend on that repository.

## Containment

- Put new checkers in `src/<project>/Diagnostics/<Subsystem>/` (or the equivalent in an established project layout). Use one checker per file, around 300 lines or less. Split a large checker by responsibility rather than making the production hot path host its implementation.
- Put the whole file inside `#if CUO_DEVTOOLS`, including imports, namespace, types, state and environment reads. A release configuration alone does not turn tooling off: the shipping variant is `-p:CuoDevTools=false`.
- Each file starts with a short header: what it checks, its settings switch/environment control, and its lifetime.
- A hot-path hook is **one line**, calling a static `void` method marked `[Conditional("CUO_DEVTOOLS")]`. Keep comparisons, shadow state, counters, logging and summary scheduling inside the checker. Do not pass a delegate to hide a block of inline checking logic.
- Conditional calls disappear along with argument evaluation when the caller is compiled without `CUO_DEVTOOLS`. Runtime `if (Checker.On)` alone does not provide that guarantee. Since the checker type itself is absent from a nodev build, surround its one-line hook with `#if CUO_DEVTOOLS` as well. C# must resolve the method before applying Conditional call omission.
- Read controls once where appropriate, and keep checking off by default. Avoid allocations on the unchecked per-frame path. Bound expensive reference passes and failure logging; include object identity, both answers and the switch state in discrepancies, plus a periodic mismatch summary.

There are two lifetimes:

**TEMPORARY** switch checkers compare a new implementation with an old path or shadow structure. Keep them through the switch's default-on proving period, then delete the checker with the old path and switch. Unknown settings keys are skipped when loading old settings files.

**LONG-LIVED** invariant checkers verify properties of the retained platform, independent of a retired implementation. Keep these as platform tests. A temporary comparison does not become long-lived merely because it has existed for a long time.

## Worked example

A checker in `src/ClassicUO.Client/Diagnostics/Ecs/ExampleCountCheck.cs` could look like this (illustrative names):

```csharp
#if CUO_DEVTOOLS
// Checks new vs old visible counts; switch: example_count, CUO_EXAMPLE_COUNT_CHECK=1.
// Lifetime: TEMPORARY; delete with the old count path and example_count switch.
using System;
using System.Diagnostics;
using ClassicUO.DevTools;

namespace ClassicUO.Game.Diagnostics.Ecs;

internal static class ExampleCountCheck
{
    private static readonly bool On =
        Environment.GetEnvironmentVariable("CUO_EXAMPLE_COUNT_CHECK") == "1";

    [Conditional("CUO_DEVTOOLS")]
    internal static void Observe(World world, int actual)
    {
        if (!On) return;
        int expected = CountUsingOldPath(world);
        if (actual != expected)
            Diag.Report("example-count", $"actual={actual}, expected={expected}");
        // Keep mismatch counters and bounded periodic summaries here too.
    }
}
#endif
```

`CountUsingOldPath` stands for the subsystem's reference implementation, owned by this temporary checker or the old production path. The production hook is only:

```csharp
#if CUO_DEVTOOLS
ExampleCountCheck.Observe(world, count);
#endif
```

The shared `ClassicUO.DevTools.Diag.Report` helper provides the common warning prefix and is itself wholly developer-only. Each checker still owns its control, comparison and summary; the helper is not a registry or delegate dispatcher. Do expensive argument construction inside the checker's enabled branch.

## Adding and retiring a checker

1. Decide the lifetime and name its deletion condition or retained invariant in the header. For a new ECS path, add its default-off settings switch and tests covering both states.
2. Add the contained checker file and a single Conditional entry point. Add one-line hooks at the necessary observation points, preserving production order and side effects. Keep supporting diagnostic types and environment reads contained too.
3. Exercise the check with known matching and deliberately mismatching inputs. Check summaries, switch state and bounded logging. Build both variants and run the normal unit suite, including the release guard.
4. For retirement, remove the old path, its temporary checker, hooks, shadow state, environment control, switch and obsolete tests together. Search by both control and type name. Keep tests of retained behavior. Update any external scenes/jobs that used the removed control in their own repository.
5. To retain a long-lived invariant, remove dependence on the old path rather than preserving it solely for checking. Keep its file, header and platform-test coverage current.

## Release guard

The normal `ClassicUO.UnitTests` build prepares a separate **Release, CuoDevTools=false** client and stages its managed assembly and first-party dependencies under the test output's `release-guard/` directory. The nested restore and build run in separate processes, so restore-generated imports are evaluated freshly and outer solution configuration mappings cannot leak into the Release graph. A guard-only props import preserves project defaults and isolates every project's intermediate/output paths, including FNA, SDL3-CS and FileEmbed. Staging copies the current client TargetPath and resolved ReferenceCopyLocalPaths after clearing the destination; it never globs a bin directory. The test inspects ECMA-335 metadata with `PEReader`, without executing the client or resolving native graphics libraries. Mono.Cecil independently inspects IL calls and resolves their target methods (including cross-assembly and generic targets); any call to a method with `[Conditional("CUO_DEVTOOLS")]` fails, even during ratchet regeneration.

It recognizes diagnostics/checker namespace and type-name patterns (including nested and generic types), plus known tooling and telemetry support types. New checker namespaces should contain `Diagnostics` and checker names should end in `Check` or `Checker`; add nonstandard tooling names to the guard predicate. It records those type definitions and `CUO_*_CHECK` literals, including strings in static initializers or unreachable methods. This is a user-string literal heuristic, not environment-read analysis. Full literals passed through helpers and used constants are caught; runtime-composed names can escape. Matching help/log strings can register even when no environment read exists. A set cannot detect extra uses of an already accepted control or extra work inside an accepted type; the separate Conditional-call check remains mandatory. It scans the client plus `ClassicUO.*` and `Uo.*` dependencies, because checker state also exists outside the client project. Ordinary production environment controls and gameplay UI types are not automatically diagnostics.

A missing staged assembly fails the test; it never silently skips. `dotnet test --no-build` uses the artifacts from the preceding build, so run a normal build/test after source changes. Synthetic assembly tests prove rejection of diagnostic names, diagnostic namespaces and environment reads, and acceptance of ordinary production code. A hook test verifies that Conditional calls remove side-effecting argument evaluation.

Run:

```text
dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release
dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -p:CuoDevTools=false
dotnet test tests/ClassicUO.UnitTests -c Release
dotnet test tests/Uo.Converter.Tests -c Release
```

Existing release debt is ratcheted in `tests/ClassicUO.UnitTests/Diagnostics/ReleaseDiagnosticsAllowlist.txt`. Each sorted row is exactly `<assembly file>|type|<full metadata name>` (nested `+` and generic arity retained) or `<assembly file>|environment|<control>`. New findings and stale rows both fail; duplicate, malformed and unsorted rows fail too. Permanent production validation/status exclusions stay in the scanner, separate from this debt list. There are no Conditional-call exemptions.

After deliberately removing debt, regenerate the tracked source file and review its diff:

```text
CUO_RELEASE_DIAGNOSTICS_REGEN=1 dotnet test tests/ClassicUO.UnitTests -c Release --filter Release_Client_Contains_No_Diagnostics
```

The environment assignment above uses shell syntax; set the same variable in your shell on Windows. Regeneration mirrors WriteSiteBudgetTests and never bypasses the Conditional-call assertion. Do not accept new diagnostic debt as a routine way to make the suite green.

Existing checkers have not been relocated in the conventions step. The external diagnostics inventory records their containment debt and proposed targets; passing a metadata guard alone does not prove their source follows the new layout and hook conventions.
