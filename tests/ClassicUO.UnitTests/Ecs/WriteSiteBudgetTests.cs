using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Ecs
{
    // T0b (docs/modern-client/ecs-transition-plan.md): a budget on direct writes to the world-state ("A") fields of
    // GameObject/Entity/Item/Mobile outside the future funnel. The count may only go down. While the ECS transition runs,
    // WriteSiteAllowlist.txt shrinks until only the funnel (WorldWriter) is left.
    //
    // The scanner reads the C# sources under src/ClassicUO.Client; it is a regex scanner without a compiler, so it is
    // approximate in both directions (see docs/modern-client/ecs-state-census.md, "Scanner limits"). Its job is to be stable
    // and to fail when somebody adds a write. If it fails on a write that is not a world-object write (a UI control with a
    // field called X), or a write that is moved from one file to another, regenerate the allowlist (CUO_WRITE_BUDGET_REGEN=1)
    // and say why in the commit.
    public class WriteSiteBudgetTests
    {
        private readonly ITestOutputHelper _output;

        public WriteSiteBudgetTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Direct_A_Field_Writes_Do_Not_Grow_Beyond_The_Allowlist()
        {
            string root = WriteSiteScanner.FindRepoRoot();
            string allowlistPath = Path.Combine(root, "tests", "ClassicUO.UnitTests", "Ecs", "WriteSiteAllowlist.txt");

            List<WriteSite> sites = WriteSiteScanner.ScanClientSources(root);
            Dictionary<string, int> found = sites.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Count());

            string dump = Environment.GetEnvironmentVariable("CUO_WRITE_BUDGET_DUMP");

            if (!string.IsNullOrEmpty(dump))
            {
                // every site as `file:line:field:receiver:kind`, to compare the scanner with a compiler-derived list
                File.WriteAllLines(dump, sites.Select(s => $"{s.File}:{s.Line}:{s.Field}:{s.Receiver}:{s.Kind}"));
            }

            if (Environment.GetEnvironmentVariable("CUO_WRITE_BUDGET_REGEN") == "1")
            {
                File.WriteAllText(allowlistPath, WriteSiteScanner.FormatAllowlist(found));
                _output.WriteLine("allowlist regenerated: " + allowlistPath);

                return;
            }

            File.Exists(allowlistPath).Should().BeTrue("the allowlist is part of the repo: " + allowlistPath);
            Dictionary<string, int> allowed = WriteSiteScanner.ParseAllowlist(File.ReadAllText(allowlistPath));

            var grown = new List<string>();
            var stale = new List<string>();

            foreach (KeyValuePair<string, int> kv in found)
            {
                allowed.TryGetValue(kv.Key, out int budget);

                if (kv.Value > budget)
                {
                    grown.Add($"{kv.Key}: {kv.Value} direct writes, budget {budget}");

                    foreach (WriteSite s in sites.Where(s => s.Key == kv.Key))
                    {
                        grown.Add($"    {s.File}:{s.Line}  {s.Receiver}.{s.Field} ({s.Kind})");
                    }
                }
            }

            foreach (KeyValuePair<string, int> kv in allowed)
            {
                found.TryGetValue(kv.Key, out int now);

                if (now < kv.Value)
                {
                    stale.Add($"{kv.Key}: now {now}, budget {kv.Value}");
                }
            }

            _output.WriteLine($"direct A-field writes: {sites.Count} in {found.Count} (file, field) pairs; budget {allowed.Values.Sum()}");

            // a downward ratchet: a removed write must lower the budget, or it would free room for a new write in the same pair
            stale.Should().BeEmpty(
                "the allowlist is above the current count: lower it with CUO_WRITE_BUDGET_REGEN=1 dotnet test --filter WriteSiteBudgetTests " +
                "(a removed write must not leave budget for a new write in the same file and field)");

            grown.Should().BeEmpty(
                "direct writes to world-object state must go through the funnel (ecs-transition-plan.md, T1); if the new write is " +
                "not a world-object write, or a write moved between files, regenerate the allowlist with CUO_WRITE_BUDGET_REGEN=1");
        }

        [Fact]
        public void Source_Discovery_Enumerates_The_Client_Files()
        {
            // guards against a scanner that scans nothing (a wrong root, a renamed folder); it does not look at counts, so a
            // successful migration (fewer writes) cannot fail it
            string root = WriteSiteScanner.FindRepoRoot();
            List<string> files = WriteSiteScanner.EnumerateClientFiles(root).Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/')).ToList();

            files.Should().Contain(new[]
            {
                "src/ClassicUO.Client/Network/PacketHandlers.cs",
                "src/ClassicUO.Client/Game/World.cs",
                "src/ClassicUO.Client/Game/GameObjects/GameObject.cs",
                "src/ClassicUO.Client/Game/GameObjects/Entity.cs",
                "src/ClassicUO.Client/Game/GameObjects/Item.cs",
                "src/ClassicUO.Client/Game/GameObjects/Mobile.cs",
                "src/ClassicUO.Client/Game/GameObjects/PlayerMobile.cs"
            });
            files.Count.Should().BeGreaterThan(100);
            files.Should().NotContain(f => f.Contains("/obj/") || f.Contains("/bin/"));
        }

        [Fact]
        public void Only_The_Funnel_Operations_Are_Exempt()
        {
            // the exemption names the funnel's operations and their release fallbacks alone (another exempt method would be an
            // unbudgeted writer)
            string root = WriteSiteScanner.FindRepoRoot();
            WriteSiteScanner.FunnelFile.Should().Be("src/ClassicUO.Client/Game/Ecs/WorldWriter.cs");
            WriteSiteScanner.FunnelClass.Should().Be("WorldWriter");
            WriteSiteScanner.FunnelOperations.Should().BeEquivalentTo("UpsertWorldItem", "PlaceWorldItem", "RemoveWorldItem", "PlaceInContainer", "ClearContainer", "ReturnHeldItem", "Equip", "ClearWornItems", "WearItem", "UpsertMobile", "PlaceMobile", "SetNotoriety", "SetMobileBody", "MoveMobile", "RemoveMobile", "SetMobileGraphic", "SetMobileDirection", "StopMobile", "CompleteStep", "SweepMobile", "SweepItem", "SetHits", "SetStatus", "SetMobileStatus", "SetMana", "SetStamina", "SetRace", "SetHealthbar", "AnimateMobile", "CompleteBoatStep", "MoveBoatPassenger", "SetBoatStepTime", "ResetBoatOffset", "FillSpellbook", "SetName", "SetPrice", "SetSpeedMode", "SetWarMode", "SetDead", "PlaceCorpseEquipment", "RekeyDeadMobile", "AdvanceAnimation", "RemoveDeadMobile", "InterpolateStep", "InterpolateBoat", "FollowBoatComponents", "FollowBoat", "JumpBoat", "IdleAnimation", "StepSound", "AdvanceCorpseFrame");
            WriteSiteScanner.FunnelFallbacks.Should().BeEquivalentTo("PlacePreT1", "RemovePreT1");

            // each listed method is a method body of the real class
            string code = WriteSiteScanner.StripCommentsAndStrings(File.ReadAllText(Path.Combine(root, "src", "ClassicUO.Client", "Game", "Ecs", "WorldWriter.cs")));
            WriteSiteScanner.MethodBodyLines(code, "WorldWriter", WriteSiteScanner.FunnelOperations).Should().HaveCount(WriteSiteScanner.FunnelOperations.Length);
            WriteSiteScanner.MethodBodyLines(code, "WorldWriter", WriteSiteScanner.FunnelFallbacks).Should().HaveCount(WriteSiteScanner.FunnelFallbacks.Length);
        }

        [Fact]
        public void The_Fallbacks_Are_Called_Only_From_The_Violation_Paths()
        {
            // coordinator, T1 round 3: the pre-T1 fallbacks are exempt like the operations, so they must not become a general
            // bypass. T1 round 4 (Codex audit round 4, #3): checked on the syntax tree; each use is a direct call in an
            // operation's body that an unconditional Reject(...) in the enclosing if's then-block precedes (UnguardedFallbackUses)
            string root = WriteSiteScanner.FindRepoRoot();
            string source = File.ReadAllText(Path.Combine(root, "src", "ClassicUO.Client", "Game", "Ecs", "WorldWriter.cs"));

            WriteSiteScanner.UnguardedFallbackUses(source, out Dictionary<string, int> calls).Should().BeEmpty();
            calls.Keys.Should().BeEquivalentTo(WriteSiteScanner.FunnelFallbacks, "each fallback has a violation path that calls it");

            // nothing else in the client names them (they are private; this also catches a copy in another class)
            foreach (string file in WriteSiteScanner.EnumerateClientFiles(root))
            {
                string rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

                if (rel != WriteSiteScanner.FunnelFile)
                {
                    string other = WriteSiteScanner.StripCommentsAndStrings(File.ReadAllText(file));
                    WriteSiteScanner.FunnelFallbacks.Should().NotContain(f => Regex.IsMatch(other, @"\b" + f + @"\b"), rel);
                }
            }
        }

        [Fact]
        public void An_Unguarded_Fallback_Use_Is_Found()
        {
            // the guarded shapes the real operations use, and Codex's round-4 counterexamples (each a use)
            const string Src =
                "internal sealed class WorldWriter {\n" +
                "    public void PlaceWorldItem(in Placement placement) {\n" +
                "        if (!Consume(placement)) { Reject(Violation.Placement, m); if (item != null) { PlacePreT1(item); } return; }\n" + // guarded
                "        if (bad) { Reject(Violation.NotHeld, m); RemovePreT1(s, out parent); return; }\n" +             // guarded
                "        if (bad) {\n" +
                "            Reject (Violation.NotHeld, m);\n" +                                                            // whitespace: still a Reject
                "            if (item != null) PlacePreT1(item);\n" +                                                      // guarded (braceless nested if)
                "        }\n" +
                "        if (fast || !Admitted(nameof(PlaceWorldItem), s)) { PlacePreT1(item); }\n" +                     // line 9: no Reject
                "        if (x) { if (y) Reject(Violation.NotHeld, m); RemovePreT1(s, out _); }\n" +                     // line 10: conditional Reject
                "        if (x) { Reject(Violation.NotHeld, m); Action a = () => PlacePreT1(item); }\n" +               // line 11: deferred lambda
                "        if (x) { Reject(Violation.NotHeld, m); Run(delegate { RemovePreT1(s, out _); }); }\n" +       // line 12: anonymous method
                "        if (placement.Id == 0) { PlacePreT1(item); }\n" +                                                // line 13: the old zero-token branch
                "        if (!(placement.Id == 0)) { PlacePreT1(item); }\n" +                                             // line 14: negated
                "        if (x) { PlacePreT1(item); Reject(Violation.Placement, m); }\n" +                               // line 15: Reject after the call
                "        if (x) { Reject(Violation.Placement, m); } else { PlacePreT1(item); }\n" +                      // line 16: the else
                "        if (x) { Reject(Violation.Placement, m); for (;;) { PlacePreT1(item); } }\n" +                  // line 17: a loop
                "        if (x) { Reject(Violation.Placement, m); var q = PlacePreT1(item) ?? y; }\n" +                  // line 18: not its own statement
                "        if (x) { Reject(Violation.Placement, m); }\n" +
                "        RemovePreT1(s, out _);\n" +                                                                      // line 20: after a closed block
                "        if (x) { Reject(Violation.Placement, m); void L() { PlacePreT1(item); } L(); }\n" +            // line 21: a local function
                "    }\n" +
                "    private void Helper() { if (x) { Reject(Violation.Placement, m); PlacePreT1(0); } }\n" +          // line 23: not an operation
                "    private void PlacePreT1(Item item) { }\n" +                                                          // the declaration
                "    private void Bypass() { Action<Item> f = PlacePreT1; }\n" +                                        // line 25: a method group
                "    private Action<Item> Bypass2 => PlacePreT1;\n" +                                                   // line 26: a delegate property
                "    private sealed class Nested { void RemoveWorldItem() { if (x) { Reject(k, m); RemovePreT1(1, out _); } } }\n" + // line 27: a nested type
                "}\n" +
                "internal static class Other { static void PlaceWorldItem() { if (x) { Reject(k, m); RemovePreT1(1, out _); } } }\n"; // line 29: another class

            WriteSiteScanner.UnguardedFallbackUses(Src, out Dictionary<string, int> calls)
                .Select(u => u.Line).Should().BeEquivalentTo(new[] { 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 21, 23, 25, 26, 27, 29 });
            calls.Should().BeEquivalentTo(new Dictionary<string, int> { ["PlacePreT1"] = 2, ["RemovePreT1"] = 1 });
        }

        [Fact]
        public void A_Fallback_Use_Under_Conditional_Compilation_Or_A_Jump_Is_Found()
        {
            // Codex audit round 5, #1 and #3: code in either build variant counts, text disabled in both is a use, and a goto
            // can skip the Reject that precedes the call textually
            const string Src =
                "internal sealed class WorldWriter {\n" +
                "    public void PlaceWorldItem(in Placement placement) {\n" +
                "#if CUO_DEVTOOLS\n" +
                "        PlacePreT1(item);\n" +                                                                     // line 4: dev build only
                "#else\n" +
                "        RemovePreT1(s, out _);\n" +                                                                // line 6: release only
                "#endif\n" +
                "#if SOMETHING_ELSE\n" +
                "        PlacePreT1(item);\n" +                                                                     // line 9: disabled in both
                "#endif\n" +
                "        if (bad) { Reject(Violation.Placement, m); PlacePreT1(item); }\n" +                        // line 11: no guard since the funnel-2 audit (#4): the method has #if SOMETHING_ELSE
                "    }\n" +
                "    public void RemoveWorldItem(Item item, out Entity parent) {\n" +
                "        if (bad) {\n" +
                "            if (fast) goto skip;\n" +
                "            Reject(Violation.NotHeld, m);\n" +
                "        skip: ;\n" +
                "            RemovePreT1(s, out parent);\n" +                                                        // line 18: the jump skips Reject
                "        }\n" +
                "    }\n" +
                "#if CUO_DEVTOOLS\n" +
                "    public void PlaceInContainer() { if (bad) { Reject(Violation.Placement, m); PlacePreT1(item); } }\n" + // line 22: guarded (dev)
                "#endif\n" +
                "}\n";

            WriteSiteScanner.UnguardedFallbackUses(Src, out Dictionary<string, int> calls)
                .Select(u => u.Line).Should().BeEquivalentTo(new[] { 4, 6, 9, 11, 18 });
            calls.Should().BeEquivalentTo(new Dictionary<string, int> { ["PlacePreT1"] = 1 });
        }

        [Fact]
        public void A_Guard_That_Depends_On_An_Unexamined_Symbol_Is_No_Guard()
        {
            // Codex audit of funnel-2, #4: the parses leave DEBUG undefined, so `#if !DEBUG` keeps the Reject in both while a
            // Debug build drops it and still runs the fallback. Any other symbol in an operation's #if/#elif: no guarded call
            const string Src =
                "internal sealed class WorldWriter {\n" +
                "    public void PlaceWorldItem(in Placement placement) {\n" +
                "        if (bad)\n" +
                "        {\n" +
                "#if !DEBUG\n" +
                "            Reject(Violation.Placement, m);\n" +
                "#endif\n" +
                "            PlacePreT1(item);\n" +                                                                  // line 8: Reject gone in Debug
                "        }\n" +
                "    }\n" +
                "    public void RemoveWorldItem(Item item, out Entity parent) {\n" +
                "        if (bad) {\n" +
                "#if CUO_DEVTOOLS\n" +
                "#elif TRACE\n" +
                "#endif\n" +
                "            Reject(Violation.NotHeld, m);\n" +
                "            RemovePreT1(s, out parent);\n" +                                                        // line 17: another symbol in an #elif
                "        }\n" +
                "    }\n" +
                "    public void PlaceInContainer() {\n" +
                "#if CUO_DEVTOOLS\n" +
                "        Log(x);\n" +
                "#endif\n" +
                "        if (bad) { Reject(Violation.Placement, m); PlacePreT1(item); }\n" +                        // line 24: guarded (CUO_DEVTOOLS only)
                "    }\n" +
                "}\n";

            WriteSiteScanner.UnguardedFallbackUses(Src, out Dictionary<string, int> calls)
                .Select(u => u.Line).Should().BeEquivalentTo(new[] { 8, 17 });
            calls.Should().BeEquivalentTo(new Dictionary<string, int> { ["PlacePreT1"] = 1 });
        }

        [Fact]
        public void The_Exemption_Is_The_Operation_Bodies_Not_The_File()
        {
            const string Src =
                "namespace N {\n" +
                "internal sealed class WorldWriter {\n" +
                "    public Item UpsertWorldItem(uint s) {\n" +
                "        Item item = _world.GetOrCreateItem(s);\n" +
                "        item.Graphic = 1;\n" +            // line 5: the operation: exempt
                "        return item;\n" +
                "    }\n" +
                "    private void Helper(Item item) {\n" +
                "        item.Hue = 2;\n" +                // line 9: another method of the funnel: counted
                "        UpsertWorldItem(1);\n" +
                "    }\n" +
                "    private sealed class Nested { void RemoveWorldItem(Item item) { item.X = 3; } }\n" + // line 12: a nested type: counted
                "    private void RemovePreT1(Item item) { item.Z = 5; }\n" + // line 13: a release fallback: exempt
                "}\n" +
                "internal static class Other { static void PlaceWorldItem(Item item) { item.Y = 4; } }\n" + // line 15: another class: counted
                "}\n";

            WriteSiteScanner.ScanSource(Src, WriteSiteScanner.FunnelFile, new HashSet<string> { "Player" }, false)
                .Select(s => (s.Field, s.Line)).Should().BeEquivalentTo(new[] { ("Hue", 9), ("X", 12), ("Y", 15) });

            // the same source in any other file: every write counts
            WriteSiteScanner.ScanSource(Src, "src/ClassicUO.Client/Game/Other.cs", new HashSet<string> { "Player" }, false).Should().HaveCount(5);
        }

        [Fact]
        public void Backing_Field_Writes_Outside_Their_Checked_Setter_Are_Found()
        {
            const string Src =
                "partial class GameObject {\n" +
                "    public ushort Graphic { get => _graphic; set { FunnelCheck.Write(this, FunnelField.Graphic); _graphic = value; } }\n" +
                "    public float OffsetX { get => _offset.X; set { FunnelCheck.Write(this, FunnelField.OffsetX); _offset.X = value; } }\n" +
                "    public uint Serial { get => _serial; set { FunnelCheck.WriteSerial(this, value); _serial = value; } }\n" +
                "    void F() { _graphic = 3; _offset.Y = 1; _amount++; }\n" +
                "    public ushort Hue { get => _hue; set { FunnelCheck.Write(this, FunnelField.Hue); _graphic = value; } }\n" + // another field's setter
                "}\n";

            // inside the declaring type (GameObject declares _graphic, _offset; _amount is Item's), in any file
            foreach (string file in new[] { "GameObject.cs", "Views/View.cs" })
            {
                WriteSiteScanner.ScanSource(Src, "src/ClassicUO.Client/Game/GameObjects/" + file, new HashSet<string> { "Player" }, true)
                    .Select(s => (s.Field, s.Line)).Should().BeEquivalentTo(new[] { ("_graphic", 5), ("_offset", 5), ("_graphic", 6) }, file);
            }

            // a private field of the same name in another class is another field (PlayerMobile's own _amount)
            const string Player =
                "internal class PlayerMobile : Mobile {\n" +
                "    private ushort _amount;\n" +
                "    void F() { _amount++; _amount = 2; }\n" +
                "}\n";
            WriteSiteScanner.ScanSource(Player, "src/ClassicUO.Client/Game/GameObjects/PlayerMobile.cs", new HashSet<string> { "Player" }, true)
                .Should().BeEmpty();
        }

        [Fact]
        public void The_Walk_Queue_Changes_Only_Through_The_Checked_Methods()
        {
            // Codex audit round 2, #5: the checked methods' own one-line bodies are their one allowed writer of _walkSteps
            const string Src =
                "internal partial class Mobile {\n" +
                "    internal void AddStep(in Step step) { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps.AddToBack(step); }\n" +
                "    internal void ReplaceStep(int i, in Step step) { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps[i] = step; }\n" +
                "    void F() { AddStep(s); ClearStepQueue(); _walkSteps.Clear(); _walkSteps[1] = s; }\n" +
                "    void G() { ref readonly Step a = ref Steps.Front(); Step b = Steps[0]; }\n" +
                // Codex audit round 3, #2: a writable reference or an alias of the private queue is a write too: any other access counts
                "    void H() { _walkSteps.Front().Run = true; ref Step r = ref _walkSteps.Back(); var q = _walkSteps; bool c = _walkSteps[0] == b; }\n" +
                "    private readonly Deque<Step> _walkSteps = new Deque<Step>(8);\n" +
                "    public StepQueue Steps => new StepQueue(_walkSteps);\n" +
                // Codex audit round 4, #4: an allowed access on a line does not exempt the other accesses on it
                "    public StepQueue Steps2 { get { _walkSteps.Clear(); return new StepQueue(_walkSteps); } }\n" +                          // line 9: Clear
                "    internal void ClearStepQueue() { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps.Clear(); } void L() { _walkSteps.Clear(); }\n" + // line 10: L's
                "    internal void AddStep(in Step s) { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps.AddToBack(s); _walkSteps.Clear(); }\n" + // line 11: three statements: both
                "    void Sneak() { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps.Clear(); }\n" +                                     // line 12: not a checked method
                "    internal void RemoveFirstStep() { FunnelCheck.Write(this, FunnelField.Steps); Run(() => _walkSteps.Clear()); }\n" +       // line 13: in a lambda
                "    public StepQueue Steps3 => new StepQueue(_walkSteps, _walkSteps);\n" +                                                    // line 14: not the view's one argument
                // Codex audit round 5, #2: another mobile's queue, an escaping alias, a callback, a read in the checked statement
                "    internal void AddStep(Mobile target, in Step step) { FunnelCheck.Write(this, FunnelField.Steps); target._walkSteps.AddToBack(step); }\n" + // line 15
                "    internal void ClearStepQueue() { FunnelCheck.Write(this, FunnelField.Steps); Keep(_walkSteps); }\n" +                    // line 16: escapes as an argument
                "    internal void RemoveFirstStep() { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps.ForEach(Keep); }\n" +          // line 17: not a queue mutator
                "    internal void ReplaceStep(int i, in Step s) { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps[i] = _walkSteps[0]; }\n" + // line 18: two
                "    internal void AddStep(in Step s) { FunnelCheck.Write(this, FunnelField.Steps); _saved = _walkSteps; }\n" +              // line 19: an alias
                "}\n";

            WriteSiteScanner.ScanSource(Src, "src/ClassicUO.Client/Game/GameObjects/MobileAnimation.cs", new HashSet<string> { "Player" }, true)
                .Select(s => (s.Field, s.Line)).Should().BeEquivalentTo(new[]
                {
                    ("Steps", 4), ("Steps", 4), ("Steps", 4), ("Steps", 4), ("Steps", 6), ("Steps", 6), ("Steps", 6), ("Steps", 6),
                    ("Steps", 9), ("Steps", 10), ("Steps", 11), ("Steps", 11), ("Steps", 12), ("Steps", 13), ("Steps", 14), ("Steps", 14),
                    ("Steps", 15), ("Steps", 16), ("Steps", 17), ("Steps", 18), ("Steps", 18), ("Steps", 19)
                });
        }

        [Theory]
        [InlineData("MobileAnimation.cs", "Mobile", "_mana", "_stamina")]
        [InlineData("Views/MobileView.cs", "Mobile", "_mana", "_stamina")]
        [InlineData("Views/ItemView.cs", "Item", "_amount", "_container")]
        [InlineData("Views/View.cs", "GameObject", "_graphic", "_hue")]
        [InlineData("Mobile.cs", "Mobile", "_mana", "_stamina")]
        public void Backing_Field_Writes_In_Another_Partial_Declaration_Are_Found(string file, string type, string f1, string f2)
        {
            // Codex audit round 2, #2: private is per type, not per file. A partial declaration in another file (and a type
            // nested in it) can write the field past its setter.
            string src =
                "namespace ClassicUO.Game.GameObjects {\n" +
                "    internal partial class " + type + " {\n" +
                "        void F() { " + f1 + " = 1; }\n" +
                "        private sealed class Nested { void G(" + type + " o) { o." + f2 + "++; } }\n" +
                "    }\n" +
                "}\n";

            WriteSiteScanner.ScanSource(src, "src/ClassicUO.Client/Game/GameObjects/" + file, new HashSet<string> { "Player" }, true)
                .Select(s => (s.Field, s.Line)).Should().BeEquivalentTo(new[] { (f1, 3), (f2, 4) });
        }

        [Fact]
        public void A_Partial_World_Class_Outside_The_GameObjects_Folder_Is_GameObject_Code()
        {
            // Codex audit round 3, #2: bare writes are GameObject writes by the enclosing type, not by the folder
            const string Src =
                "namespace ClassicUO.Game.GameObjects {\n" +
                "    internal partial class Mobile {\n" +
                "        void F() { _mana = 1; Hits = 2; }\n" +                // line 3: both count
                "    }\n" +
                "    internal class Other { int Hits; void G() { Hits = 3; } }\n" + // line 5: another class: a bare write is its own field
                "}\n";

            WriteSiteScanner.ScanFile(Src, "src/ClassicUO.Client/Game/Managers/MobileExtras.cs", new HashSet<string> { "Player" })
                .Select(s => (s.Field, s.Line)).Should().BeEquivalentTo(new[] { ("_mana", 3), ("Hits", 3) });

            // the folder rule still holds: a GameObjects file is GameObject code throughout
            WriteSiteScanner.ScanFile(Src, "src/ClassicUO.Client/Game/GameObjects/MobileExtras.cs", new HashSet<string> { "Player" })
                .Select(s => (s.Field, s.Line)).Should().BeEquivalentTo(new[] { ("_mana", 3), ("Hits", 3), ("Hits", 5) });
        }

        // ---- the scanner's own behaviour on small sources ----

        private static List<WriteSite> Scan(string source, string path = "src/ClassicUO.Client/Game/Foo.cs") =>
            WriteSiteScanner.ScanSource(source, path, new HashSet<string> { "Player" }, insideGameObjectClass: false);

        private static string Fields(string source, string path = "src/ClassicUO.Client/Game/Foo.cs") =>
            string.Join(",", Scan(source, path).Select(s => s.Field));

        [Theory]
        [InlineData("void F(Item item) { item.Graphic = 1; }", "Graphic")]
        [InlineData("void F(Mobile m) { m.Hue |= 0x8000; }", "Hue")]
        [InlineData("void F(Entity e) { e.X += 1; e.Y--; }", "X,Y")]
        [InlineData("void F(Item item) { ++item.Z; }", "Z")]
        [InlineData("void F(Item item) { Use(ref item.Amount); }", "Amount")]
        [InlineData("void F(Item item) { Use(out item.Layer); }", "Layer")]
        [InlineData("void F(World w) { w.Player.Flags = Flags.None; }", "Flags")]
        [InlineData("void F(World w) { w.Items.Get(1).Price = 5; }", "Price")]
        [InlineData("void F(object o) { ((Item)o).Container = 7; }", "Container")]
        [InlineData("void F(PlayerMobile p) { p.Name ??= \"x\"; }", "Name")]
        [InlineData("void F(Item i) { i.X = i.Y = 0; }", "X,Y")]
        [InlineData("void F(Mobile m) { m.Steps.AddToBack(s); m.Steps.RemoveFromFront(); m.Steps.Clear(); }", "Steps,Steps,Steps")]
        [InlineData("void F(Mobile m) { m.AddStep(s); m.ReplaceStep(0, s); m.RemoveFirstStep(); m.ClearStepQueue(); }", "Steps,Steps,Steps,Steps")]
        [InlineData("void F(Mobile m) { m._walkSteps.Clear(); m._walkSteps[0] = s; }", "Steps,Steps")]
        [InlineData("void F(Mobile m) { m.Offset.Z = 4; m.Offset.X += 1; }", "Offset,Offset")]
        [InlineData("void F(Mobile m) { m.OffsetZ = 4; m.OffsetX += 1; }", "Offset,Offset")]
        public void Writes_Are_Found(string source, string expectedFields) =>
            Fields(source).Should().Be(expectedFields);

        [Theory]
        [InlineData("void F(Item item) { if (item.Graphic == 1) { } }")]
        [InlineData("void F(Item item) { if (item.Hue != 1 || item.X >= 3 || item.Y <= 4) { } }")]
        [InlineData("void F(Item item) { var f = () => item.Graphic; Func<int> g = () => item.X; }")]
        [InlineData("void F(Item item) { int g = item.Graphic; }")]
        [InlineData("void F(Item item) { Use(item.X, item.Y); }")]
        [InlineData("void F(Item item) { // item.Graphic = 1;\n }")]
        [InlineData("void F(Item item) { /* item.Graphic = 1; */ }")]
        [InlineData("void F(Item item) { string s = \"item.Graphic = 1\"; }")]
        [InlineData("void F(Item item) { string s = @\"item.Graphic = 1\"; }")]
        [InlineData("void F(Item item) { string s = $\"{item.Graphic} = 1\"; }")]
        [InlineData("void F(Item item) { char c = '='; var x = item.X; }")]
        [InlineData("void F(Control c) { c.X = 1; c.Graphic = 2; }")]
        [InlineData("void F() { var r = new Rectangle { X = 1, Y = 2 }; }")]
        [InlineData("void F(Item item) { var r = item.X == 3; }")]
        [InlineData("void F(Mobile m) { int n = m.Steps.Count; ref var s = ref m.Steps.Back(); var o = m.Offset.X; var p = m.Offset.X == 3; }")]
        [InlineData("void F(Control c) { c.Steps.Clear(); }")]
        public void Reads_Comments_Strings_And_Other_Types_Are_Not_Writes(string source) =>
            Scan(source).Should().BeEmpty();

        [Fact]
        public void Bare_Writes_Count_Only_Inside_GameObject_Classes_And_Not_In_Object_Initializers()
        {
            const string Src = "class Foo { void A() { X = 1; Y += 2; Z++; } void B() { var r = new Rectangle { X = 1, Y = 2 }; } " +
                               "void C() { var r = new Rectangle { X = 1 }; if (Hue == 3) { } } }";

            WriteSiteScanner.ScanSource(Src, "src/ClassicUO.Client/Game/GameObjects/Foo.cs", new HashSet<string> { "Player" }, true)
                .Select(s => s.Field).Should().Equal("X", "Y", "Z");
            WriteSiteScanner.ScanSource(Src, "src/ClassicUO.Client/Game/Other.cs", new HashSet<string> { "Player" }, false)
                .Should().BeEmpty();
        }

        [Fact]
        public void Receiverless_Prefix_Ref_Out_And_Private_State_Writes_Inside_A_GameObject_Class_Are_Found()
        {
            const string Src = "class C { private byte _animationGroup = 0xFF; void F() { ++AnimIndex; Interlocked.Exchange(ref LastAnimationChangeTime, 0L); " +
                               "Reset(out X); _animationGroup = 3; _lastAnimationIdleDelay += 5; (Y, Z) = (1, 2); var d = ref X ? ref a : ref b; _isSA_Poisoned = v; } void G(out Direction endDir, ref Layer l) { } }";

            WriteSiteScanner.ScanSource(Src, "src/ClassicUO.Client/Game/GameObjects/Foo.cs", new HashSet<string> { "Player" }, true)
                .Select(s => s.Field).Should().BeEquivalentTo(
                    "AnimIndex", "LastAnimationChangeTime", "X", "_animationGroup", "_lastAnimationIdleDelay", "Y", "Z", "_isSA_Poisoned");
            WriteSiteScanner.ScanSource(Src, "src/ClassicUO.Client/Game/Other.cs", new HashSet<string> { "Player" }, false).Should().BeEmpty();
        }

        [Fact]
        public void Dotted_Deconstruction_NullConditional_Prefix_And_Ref_Writes_Are_Found()
        {
            Fields("void F(Item i) { (i.X, i.Y) = (1, 2); i?.Hue = 3; ++i.Amount; Interlocked.Exchange(ref i.Amount, 0); }")
                .Split(',').Should().BeEquivalentTo("X", "Y", "Hue", "Amount", "Amount");
            Fields("void F(Item i, Control c) { var (a, b) = (i.X, i.Y); (c.X, c.Y) = (1, 2); }").Should().BeEmpty();
        }

        [Fact]
        public void Locals_Typed_Through_Var_And_Foreach_Are_Recognised()
        {
            const string Src = "void F(World world) { var p = world.Player; p.X = 1; var m = world.Mobiles.Get(1); m.Y = 2; " +
                               "foreach (var mob in world.Mobiles.Values) { mob.Z = 3; } var r = new Rectangle(); r.X = 4; }";

            Fields(Src).Should().Be("X,Y,Z");
        }

        [Fact]
        public void Allowlist_Round_Trips()
        {
            var counts = new Dictionary<string, int> { ["a/b.cs|X"] = 3, ["a/c.cs|Graphic"] = 1 };

            WriteSiteScanner.ParseAllowlist(WriteSiteScanner.FormatAllowlist(counts)).Should().BeEquivalentTo(counts);
        }
    }

    internal readonly record struct WriteSite(string File, int Line, string Field, string Receiver, string Kind)
    {
        public string Key => File + "|" + Field;
    }

    internal static class WriteSiteScanner
    {
        // the world-state ("A") members of GameObject/Entity/Item/Mobile (ecs-state-census.md, section 2)
        internal static readonly string[] AFields =
        {
            "Graphic", "Hue", "X", "Y", "Z", "Offset", "OffsetX", "OffsetY", "OffsetZ", "Flags", "Serial", "Name", "Direction", "Hits", "HitsMax",
            "Amount", "Container", "Layer", "Price", "IsDamageable", "LightID", "UsedLayer", "DisplayedGraphic", "IsMulti",
            "Race", "SpeedMode", "Stamina", "StaminaMax", "Mana", "ManaMax", "IsRenamable", "IsFemale", "IsRunning",
            "NotorietyFlag", "IsDead", "InWarMode", "AnimIndex", "ExecuteAnimation", "LastAnimationChangeTime",
            "LastStepTime", "AnimationFrameCount", "AnimationFromServer", "LastStepSoundTime", "StepSoundOffset",
            // private A state (written by name only inside the classes): the animation timer, the pushed-input backing fields
            "_animationGroup", "_animationInterval", "_animationRepeat", "_animationRepeateMode", "_animationRepeatModeCount",
            "_isAnimationForwardDirection", "_lastAnimationIdleDelay", "_isSA_Poisoned", "_isDead", "_isFemale", "_isRunning",
            "_notorietyFlag", "_inWarMode", "_direction", "_hits", "_hitsMax", "_isMulti", "_displayedGraphic",
            // T1: the backing fields the funnel-check setters introduced (Codex audit of slice 1, #3): a write outside the
            // field's own setter is a direct write that neither the setter's check nor the property names would catch
            "_graphic", "_hue", "_x", "_y", "_z", "_offset", "_flags", "_serial", "_name", "_animIndex", "_executeAnimation",
            "_lastAnimationChangeTime", "_lastStepTime", "_amount", "_container", "_isDamageable", "_layer", "_lightID", "_price",
            "_usedLayer", "_race", "_speedMode", "_stamina", "_staminaMax", "_mana", "_manaMax", "_isRenamable",
            "_animationFrameCount", "_animationFromServer", "_lastStepSoundTime", "_stepSoundOffset"
        };

        // T1: the backing fields of the one-line checked setters (`set { FunnelCheck.Write(this, FunnelField.X); _x = value; }`)
        // and their declaring type: that setter is their one allowed writer. They are private, so only code inside their
        // declaring type can write them, in any of its partial declarations (Mobile's in MobileAnimation.cs and
        // Views/MobileView.cs, GameObject's in Views/View.cs) and its nested types (Codex audit round 2, #2). A field of the same
        // name in another type (PlayerMobile's own _amount) is not one of them.
        internal static readonly Dictionary<string, string> CheckedBackingFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_graphic"] = "GameObject", ["_hue"] = "GameObject", ["_x"] = "GameObject", ["_y"] = "GameObject",
            ["_z"] = "GameObject", ["_offset"] = "GameObject",
            ["_flags"] = "Entity", ["_serial"] = "Entity", ["_name"] = "Entity", ["_animIndex"] = "Entity",
            ["_executeAnimation"] = "Entity", ["_lastAnimationChangeTime"] = "Entity", ["_lastStepTime"] = "Entity",
            ["_amount"] = "Item", ["_container"] = "Item", ["_isDamageable"] = "Item", ["_layer"] = "Item",
            ["_lightID"] = "Item", ["_price"] = "Item", ["_usedLayer"] = "Item",
            ["_race"] = "Mobile", ["_speedMode"] = "Mobile", ["_stamina"] = "Mobile", ["_staminaMax"] = "Mobile",
            ["_mana"] = "Mobile", ["_manaMax"] = "Mobile", ["_isRenamable"] = "Mobile", ["_animationFrameCount"] = "Mobile",
            ["_animationFromServer"] = "Mobile", ["_lastStepSoundTime"] = "Mobile", ["_stepSoundOffset"] = "Mobile"
        };

        private static readonly Regex CheckedSetterLine = new Regex(
            @"\bset\s*\{\s*FunnelCheck\.(?:WriteSerial\s*\(\s*this\s*,\s*value|Write\s*\(\s*this\s*,\s*FunnelField\.(?<p>\w+))\s*\)\s*;\s*(?<f>_\w+)(?:\s*\.\s*\w+)?\s*=\s*value\s*;\s*\}",
            RegexOptions.Compiled);

        // GameEffect and its subclasses are GameObjects but not Entities: only the GameObject fields count there
        private static readonly HashSet<string> EffectFields = new HashSet<string> { "Graphic", "Hue", "X", "Y", "Z", "Offset" };

        private static readonly HashSet<string> EffectFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GameEffect.cs", "MovingEffect.cs", "FixedEffect.cs", "DragEffect.cs", "LightningEffect.cs"
        };

        // files in src/ClassicUO.Client/Game/GameObjects that are not GameObject classes (a bare `X = 1` there is not this.X)
        private static readonly HashSet<string> NotGameObjectFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EntityTextContainer.cs", "RenderedText.cs", "TextObject.cs", "MobileTable.cs", "IsometricLight.cs", "House.cs", "EntityCollection.cs"
        };

        // T1: the mutation funnel (ecs-transition-plan.md). The writes inside the bodies of these methods of this class in this
        // file are the allowed ones; every other write in the file (another method, a nested type, another class) counts.
        // Keep this to the funnel's operations: a method listed here can write world state without limit.
        internal const string FunnelFile = "src/ClassicUO.Client/Game/Ecs/WorldWriter.cs";
        internal const string FunnelClass = "WorldWriter";

        internal static readonly string[] FunnelOperations = { "UpsertWorldItem", "PlaceWorldItem", "RemoveWorldItem", "PlaceInContainer", "ClearContainer", "ReturnHeldItem", "Equip", "ClearWornItems", "WearItem", "UpsertMobile", "PlaceMobile", "SetNotoriety", "SetMobileBody", "MoveMobile", "RemoveMobile", "SetMobileGraphic", "SetMobileDirection", "StopMobile", "CompleteStep", "SweepMobile", "SweepItem", "SetHits", "SetStatus", "SetMobileStatus", "SetMana", "SetStamina", "SetRace", "SetHealthbar", "AnimateMobile", "CompleteBoatStep", "MoveBoatPassenger", "SetBoatStepTime", "ResetBoatOffset", "FillSpellbook", "SetName", "SetPrice", "SetSpeedMode", "SetWarMode", "SetDead", "PlaceCorpseEquipment", "RekeyDeadMobile", "AdvanceAnimation", "RemoveDeadMobile", "InterpolateStep", "InterpolateBoat", "FollowBoatComponents", "FollowBoat", "JumpBoat", "IdleAnimation", "StepSound", "AdvanceCorpseFrame" };

        // T1 (coordinator, round 3): the release build's pre-T1 fallbacks of the operations, exempt like them (funnel code).
        // They are reached only after a rejection; The_Fallbacks_Are_Called_Only_From_The_Violation_Paths pins that, so they
        // cannot become a general bypass. T1 round 4: a nesting is rejected without a fallback, so UpsertPreT1 and
        // PlaceInContainerPreT1 are gone; the placement and removal violations keep theirs.
        internal static readonly string[] FunnelFallbacks = { "PlacePreT1", "RemovePreT1" };

        private static readonly string FieldAlt = string.Join("|", AFields);
        private const string WorldTypes = "Item|Mobile|Entity|PlayerMobile|GameObject";
        private const string Op = @"(?:=(?![=>])|\+=|-=|\*=|/=|%=|&=|\|=|\^=|<<=|>>=|\?\?=|\+\+|--)";

        // `recv.Field op`
        private static readonly Regex Write = new Regex(@"\.\s*(?<f>" + FieldAlt + @")\b\s*(?<op>" + Op + ")", RegexOptions.Compiled);
        // `recv.Offset.X op`: a write to a member of the Vector3
        private static readonly Regex SubWrite = new Regex(@"\.\s*Offset\s*\.\s*\w+\s*(?<op>" + Op + ")", RegexOptions.Compiled);
        // `++recv.Field`, `ref recv.Field`, `out recv.Field`
        private static readonly Regex Prefix = new Regex(
            @"(?:(?<![\w+])(?:\+\+|--)|\b(?:ref|out))\s*(?<recv>[\w\.\s\(\)\[\],]*?)\.\s*(?<f>" + FieldAlt + @")\b", RegexOptions.Compiled);
        // `Field op` and `Offset.X op` with no receiver (inside a GameObject class)
        private static readonly Regex Bare = new Regex(@"(?<![\w\.])(?<f>" + FieldAlt + @")\b\s*(?<op>" + Op + ")", RegexOptions.Compiled);
        // `recv.Steps.AddToBack(`, `Steps.Clear(` ...: calls that change the walk queue (Back()/Front() return refs and are mostly reads)
        private const string StepsMutators = @"(?:AddToBack|AddToFront|RemoveFromBack|RemoveFromFront|Clear|Insert|RemoveAt)\b";
        // T1 (Codex audit round 2, #5): Steps is a read-only view; the walk queue changes through Mobile's checked methods,
        // whose calls count like a setter's, and their one-line bodies write the private queue (_walkSteps), which counts anywhere else
        private const string StepsMethods = @"(?:AddStep|RemoveFirstStep|ReplaceStep|ClearStepQueue)\s*\(";
        private static readonly Regex StepsCall = new Regex(@"\.\s*(?:Steps\s*\.\s*" + StepsMutators + "|" + StepsMethods + ")", RegexOptions.Compiled);
        private static readonly Regex BareStepsCall = new Regex(@"(?<![\w\.])(?<!\bvoid\s+)(?:Steps\s*\.\s*" + StepsMutators + "|" + StepsMethods + ")", RegexOptions.Compiled);
        // any access of the private queue (Codex audit round 3, #2: `_walkSteps.Front().Run = true`, `ref _walkSteps.Back()`, an
        // alias), except its declaration, the read-only view and the checked methods
        private static readonly Regex StepsBackingCall = new Regex(@"(?<!\w)_walkSteps\b", RegexOptions.Compiled);
        // `++Field`, `Interlocked.Exchange(ref Field, 0)`, `Reset(out Field)` with no receiver (inside a GameObject class)
        private static readonly Regex BarePrefix = new Regex(
            @"(?:(?<![\w+])(?:\+\+|--)|\b(?:ref|out))\s*(?<f>" + FieldAlt + @")\b(?!\s+[A-Za-z_])", RegexOptions.Compiled);
        // `(a.X, a.Y) = (1, 2)`: the left tuple of a deconstruction
        private static readonly Regex Deconstruct = new Regex(@"\((?<l>[^()=;{}]*)\)\s*=(?![=>])", RegexOptions.Compiled);
        private static readonly Regex TupleItem = new Regex(@"^(?:(?<recv>.+)\.\s*)?(?<f>" + FieldAlt + @")$", RegexOptions.Compiled);
        private static readonly Regex BareSub = new Regex(@"(?<![\w\.])Offset\s*\.\s*\w+\s*(?<op>" + Op + ")", RegexOptions.Compiled);

        // `Type name` declarations: locals, parameters, fields, foreach variables, pattern variables
        private static readonly Regex Decl = new Regex(
            @"(?<![\w.])(?<t>[A-Za-z_][\w.]*(?:<[^<>;()]*(?:<[^<>;()]*>[^<>;()]*)*>)?(?:\[[,\s]*\])*\??)\s+(?<n>[A-Za-z_]\w*)\s*(?<next>=(?!=)|;|,|\)|\bin\b|\{|=>)",
            RegexOptions.Compiled);
        private static readonly Regex WorldType = new Regex(@"(?<![\w.])(?:" + WorldTypes + @")(?![\w])", RegexOptions.Compiled);
        private static readonly Regex VarInit = new Regex(@"\s*=\s*(?<e>[^;]*);", RegexOptions.Compiled);
        private static readonly Regex NewCollection = new Regex(@"^new\s+[\w.<>,\s]*\b(?:" + WorldTypes + @")\b", RegexOptions.Compiled);
        private static readonly Regex ForeachSource = new Regex(@"\b(?:Mobiles|Items)\b|\bTextOwners\b", RegexOptions.Compiled);
        private static readonly Regex Producer = new Regex(
            @"(?:\bPlayer\b|\.Items\s*\.\s*(?:Get|Values)|\.Mobiles\s*\.\s*(?:Get|Values)|\.Get\s*\(|\bGetOrCreate(?:Item|Mobile)\s*\(|\b(?:Item|Mobile|PlayerMobile)\.Create\s*\(|new\s+(?:Item|Mobile|PlayerMobile)\s*\(|\bFind(?:Item\w*|Bandage|PreferredItemByCliloc)\s*\(|\bGetSecureTradeBox\s*\(|\bGetCorpseObject\s*\()",
            RegexOptions.Compiled);
        private static readonly Regex GetterCall = new Regex(
            @"(?:\.|^)(?:Get|GetOrCreateItem|GetOrCreateMobile|FindItem\w*|FindBandage|GetSecureTradeBox|GetCorpseObject|Create)$", RegexOptions.Compiled);
        private static readonly Regex CastReceiver = new Regex(@"^\(+(?:" + WorldTypes + @")\)\w+\)*$|^\(\w+as(?:" + WorldTypes + @")\)$", RegexOptions.Compiled);
        private static readonly Regex Space = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex QuestionAfter = new Regex(@"\G\s*\?(?!\.)", RegexOptions.Compiled);
        private static readonly Regex NextName = new Regex(@"\G\s*(?<n>[A-Za-z_]\w*)\s*(?=[=,;])", RegexOptions.Compiled);
        private static readonly Regex LastWord = new Regex(@"([A-Za-z_]\w*)$", RegexOptions.Compiled);

        private static readonly HashSet<string> Keywords = new HashSet<string>(
            ("return new throw else case in out ref yield await goto typeof sizeof default using lock do is as not and or when from select where let " +
             "into on equals by ascending descending params this base null true false operator implicit explicit async static readonly public private " +
             "protected internal virtual override abstract sealed partial const volatile unsafe extern fixed checked unchecked stackalloc nameof").Split(' '));

        internal static string FindRepoRoot()
        {
            string env = Environment.GetEnvironmentVariable("CUO_REPO_ROOT");

            if (!string.IsNullOrEmpty(env) && IsRoot(env))
            {
                return Path.GetFullPath(env);
            }

            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                for (DirectoryInfo d = new DirectoryInfo(start); d != null; d = d.Parent)
                {
                    if (IsRoot(d.FullName))
                    {
                        return d.FullName;
                    }
                }
            }

            throw new InvalidOperationException(
                "Cannot find the repository root (a folder with src/ClassicUO.Client/ClassicUO.Client.csproj and " +
                "tests/ClassicUO.UnitTests) above " + AppContext.BaseDirectory + " or " + Directory.GetCurrentDirectory() + "; set CUO_REPO_ROOT.");
        }

        private static bool IsRoot(string dir) =>
            File.Exists(Path.Combine(dir, "src", "ClassicUO.Client", "ClassicUO.Client.csproj"))
            && Directory.Exists(Path.Combine(dir, "tests", "ClassicUO.UnitTests"));

        internal static IEnumerable<string> EnumerateClientFiles(string root)
        {
            string sep = Path.DirectorySeparatorChar.ToString();

            return Directory.EnumerateFiles(Path.Combine(root, "src", "ClassicUO.Client"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep))
                .OrderBy(f => f, StringComparer.Ordinal);
        }

        internal static List<WriteSite> ScanClientSources(string root)
        {
            var result = new List<WriteSite>();
            var files = EnumerateClientFiles(root).Select(f => (Path: f, Text: File.ReadAllText(f))).ToList();

            // member names (fields and properties, indented as class members) whose declared type is a world object, from every
            // file: the receivers of chains such as `SelectedObject.HealthbarObject.X`, `_world.Player.X`
            var globalMembers = new HashSet<string> { "Player" };
            var member = new Regex(@"^ {8}(?:public|internal|private|protected)[ \w]*?\b(?:" + WorldTypes + @")\??\s+(?<n>\w+)\s*(?:;|=[^>]|\{)", RegexOptions.Multiline);

            foreach ((string _, string text) in files)
            {
                foreach (Match m in member.Matches(text))
                {
                    globalMembers.Add(m.Groups["n"].Value);
                }
            }

            foreach ((string path, string text) in files)
            {
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                result.AddRange(ScanFile(text, rel, globalMembers));
            }

            return result;
        }

        // a declaration of a world-object type (a partial one too)
        private static readonly Regex WorldTypeDecl = new Regex(@"\bclass\s+(?:" + WorldTypes + @")\b", RegexOptions.Compiled);
        private static readonly HashSet<string> WorldTypeNames = new HashSet<string>(WorldTypes.Split('|'), StringComparer.Ordinal);

        /// <summary>
        /// One client file. A file in Game/GameObjects (except <see cref="NotGameObjectFiles"/>) is GameObject code throughout:
        /// a bare `X = 1` there is this.X. Elsewhere, the bodies of the world-object types declared in the file (a partial
        /// declaration of Mobile in another folder, Codex audit round 3, #2) are GameObject code, the rest is not.
        /// </summary>
        internal static List<WriteSite> ScanFile(string text, string rel, HashSet<string> globalMembers)
        {
            string name = Path.GetFileName(rel);
            bool gameObjectFile = rel.StartsWith("src/ClassicUO.Client/Game/GameObjects/", StringComparison.Ordinal) && !NotGameObjectFiles.Contains(name);
            List<WriteSite> sites = ScanSource(text, rel, globalMembers, gameObjectFile);

            if (!gameObjectFile)
            {
                string code = StripCommentsAndStrings(text);

                if (WorldTypeDecl.IsMatch(code))
                {
                    List<(string Name, int From, int To)> world = TypeBodies(code).Where(t => WorldTypeNames.Contains(t.Name)).ToList();
                    bool Inside(WriteSite s) => world.Exists(t => s.Line >= t.From && s.Line <= t.To);
                    sites = sites.Where(s => !Inside(s)).Concat(ScanSource(text, rel, globalMembers, true).Where(Inside)).ToList();
                }
            }

            if (EffectFiles.Contains(name))
            {
                sites = sites.Where(s => s.Kind != "bare" || EffectFields.Contains(s.Field)).ToList();
            }

            return sites;
        }

        internal static List<WriteSite> ScanSource(string source, string path, HashSet<string> globalMembers, bool insideGameObjectClass)
        {
            List<WriteSite> sites = ScanSourceAll(source, path, globalMembers, insideGameObjectClass);
            string[] lines = source.Split('\n');

            // a checked backing field: written by its own one-line setter, or a field of the same name in another type
            List<(string Name, int From, int To)> types = null;
            sites.RemoveAll(s =>
            {
                if (!CheckedBackingFields.TryGetValue(s.Field, out string declaringType))
                {
                    return false;
                }

                types ??= TypeBodies(StripCommentsAndStrings(source));

                return !types.Exists(t => t.Name == declaringType && s.Line >= t.From && s.Line <= t.To)
                       || (s.Line - 1 < lines.Length && IsOwnSetter(lines[s.Line - 1], s.Field));
            });

            // T1: the funnel's operations
            if (path == FunnelFile)
            {
                List<(int From, int To)> bodies = MethodBodyLines(StripCommentsAndStrings(source), FunnelClass, FunnelOperations.Concat(FunnelFallbacks).ToArray());
                sites.RemoveAll(s => bodies.Exists(b => s.Line >= b.From && s.Line <= b.To));
            }

            return sites;
        }

        private static readonly Regex TypeDecl = new Regex(@"\b(?:class|struct|record|interface)\s+(?<n>[A-Za-z_]\w*)[^{;]*\{", RegexOptions.Compiled);

        /// <summary>
        /// The types declared in <paramref name="code"/> (without comments and strings), each declaration (a partial one too)
        /// with the line range of its body; a nested type's range lies inside its container's.
        /// </summary>
        internal static List<(string Name, int From, int To)> TypeBodies(string code)
        {
            var result = new List<(string, int, int)>();

            foreach (Match m in TypeDecl.Matches(code))
            {
                int open = m.Index + m.Length - 1;
                result.Add((m.Groups["n"].Value, LineOf(code, open), LineOf(code, MatchBrace(code, open, '{', '}'))));
            }

            return result;
        }

        private static bool IsOwnSetter(string line, string field)
        {
            foreach (Match m in CheckedSetterLine.Matches(line))
            {
                // the setter of the property the field backs: Graphic -> _graphic, OffsetX -> _offset, WriteSerial -> _serial
                string prop = m.Groups["p"].Success ? m.Groups["p"].Value : "Serial";

                if (prop is "OffsetX" or "OffsetY" or "OffsetZ")
                {
                    prop = "Offset";
                }

                if (m.Groups["f"].Value == field && field == "_" + char.ToLowerInvariant(prop[0]) + prop.Substring(1))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The uses of the <see cref="FunnelFallbacks"/> in <paramref name="source"/> that are not a guarded call (T1 round 4,
        /// Codex audit round 4, #3: syntax trees, not text). A guarded call is a direct call (`Name(...)`) that is the whole
        /// expression of its statement, in the body of an operation (<see cref="FunnelOperations"/>) declared directly in
        /// <see cref="FunnelClass"/>, with no lambda, anonymous method or local function around it, reached from the
        /// then-block of an `if` only through nested then-branches and blocks, where that then-block runs an unconditional
        /// `Reject(...)` statement before the statement holding the call. Anything else that names a fallback is a use:
        /// a method group, a delegate property, nameof, a call under another condition, in a loop, an else, a switch or a try.
        /// The declarations are not uses (their names are tokens, not references). <paramref name="calls"/>: the guarded calls
        /// per fallback.
        /// </summary>
        internal static List<(int Line, string Use)> UnguardedFallbackUses(string source, out Dictionary<string, int> calls)
        {
            var bad = new List<(int, string)>();
            calls = new Dictionary<string, int>(StringComparer.Ordinal);
            var fallbacks = new HashSet<string>(FunnelFallbacks, StringComparer.Ordinal);
            var seenCalls = new HashSet<int>();
            var seenBad = new HashSet<(int, string)>();

            // T1 funnel-2 (Codex audit round 5, #1): conditional compilation. The source is parsed with no symbols and with
            // CUO_DEVTOOLS (the client's two build variants), so the code under `#if CUO_DEVTOOLS` and under its `#else` is
            // each analysed in one of the parses; a fallback named in text that both parses disable (another symbol) is a use.
            SyntaxNode plain = CSharpSyntaxTree.ParseText(source).GetRoot();
            SyntaxNode dev = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: new[] { "CUO_DEVTOOLS" })).GetRoot();

            foreach (SyntaxNode root in new[] { plain, dev })
            {
                foreach (IdentifierNameSyntax id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    string name = id.Identifier.ValueText;

                    if (!fallbacks.Contains(name))
                    {
                        continue;
                    }

                    if (IsGuardedFallbackCall(id))
                    {
                        if (seenCalls.Add(id.SpanStart))
                        {
                            calls[name] = calls.TryGetValue(name, out int n) ? n + 1 : 1;
                        }
                    }
                    else if (seenBad.Add((LineAt(root, id.SpanStart), name)))
                    {
                        bad.Add((LineAt(root, id.SpanStart), name));
                    }
                }
            }

            foreach (SyntaxTrivia t in plain.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
            {
                foreach (Match m in Regex.Matches(t.ToFullString(), @"\b\w+\b"))
                {
                    int pos = t.SpanStart + m.Index;

                    if (fallbacks.Contains(m.Value)
                        && dev.FindTrivia(pos).IsKind(SyntaxKind.DisabledTextTrivia)
                        && seenBad.Add((LineAt(plain, pos), m.Value)))
                    {
                        bad.Add((LineAt(plain, pos), m.Value));
                    }
                }
            }

            return bad;
        }

        private static int LineAt(SyntaxNode root, int position) =>
            root.SyntaxTree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0)).StartLinePosition.Line + 1;

        private static bool IsGuardedFallbackCall(IdentifierNameSyntax id)
        {
            if (id.Parent is not InvocationExpressionSyntax inv || inv.Expression != id
                || inv.Parent is not (ExpressionStatementSyntax or ReturnStatementSyntax))
            {
                return false;
            }

            var stmt = (StatementSyntax)inv.Parent;
            MethodDeclarationSyntax method = stmt.FirstAncestorOrSelf<MethodDeclarationSyntax>();

            if (method?.Body == null
                || Array.IndexOf(FunnelOperations, method.Identifier.ValueText) < 0
                || method.Parent is not ClassDeclarationSyntax cls
                || cls.Identifier.ValueText != FunnelClass
                || cls.Parent is TypeDeclarationSyntax
                || stmt.Ancestors().TakeWhile(a => a != method).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                // T1 funnel-2 (Codex audit round 5, #3): "a Reject before the call" is textual order, and a jump can skip it
                // (`if (fast) goto skip; Reject(...); skip: Fallback();`), so an operation with a goto or a label has no
                // guarded call at all
                || method.Body.DescendantNodes().Any(d => d is GotoStatementSyntax or LabeledStatementSyntax)
                // Codex audit of funnel-2, #4: the two parses leave every other symbol undefined, so a Reject under
                // `#if !DEBUG` (present in both parses, gone in a Debug build) would look like a guard while the fallback
                // stays enabled. Conservatively: an operation with a conditional directive naming any symbol other than
                // CUO_DEVTOOLS (the one the parses examine) has no guarded call at all
                || DependsOnUnexaminedSymbols(method.Body))
            {
                return false;
            }

            SyntaxNode child = stmt;

            for (SyntaxNode n = stmt.Parent; n != null && n != method.Body; child = n, n = n.Parent)
            {
                if (n is BlockSyntax block)
                {
                    if (block.Parent is IfStatementSyntax guard && guard.Statement == block)
                    {
                        int at = block.Statements.IndexOf((StatementSyntax)child);

                        if (block.Statements.Take(at).Any(IsRejectStatement))
                        {
                            return true;
                        }
                    }

                    continue;
                }

                // a nested `if (...) Call(...);` or `if (...) { ... }` inside the guarded block: its then-branch only
                if (n is IfStatementSyntax inner && inner.Statement == child)
                {
                    continue;
                }

                return false; // an else, a loop, a switch, a try, a using, ...
            }

            return false;
        }

        // an `#if`/`#elif` in the body whose condition names a symbol the two parses do not vary (anything but CUO_DEVTOOLS)
        private static bool DependsOnUnexaminedSymbols(SyntaxNode body) =>
            body.DescendantTrivia(descendIntoTrivia: true)
                .Select(t => t.GetStructure())
                .OfType<ConditionalDirectiveTriviaSyntax>()
                .Any(d => d.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(n => n.Identifier.ValueText != "CUO_DEVTOOLS"));

        // `Reject(...);` as a statement of its own (not under a condition)
        private static bool IsRejectStatement(StatementSyntax s) =>
            s is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Reject" } } };

        /// <summary>
        /// T1 round 4 (Codex audit round 4, #4): whether the `_walkSteps` token at <paramref name="position"/> of
        /// <paramref name="root"/> is one of the queue's allowed accesses, by its own syntax node (not its line): the field's
        /// declaration, the one argument of `new StepQueue(_walkSteps)` (the read-only view), or an access inside the second
        /// and last statement of a checked method (<see cref="StepsMethodNames"/>) whose body is exactly
        /// `{ FunnelCheck.Write(this, FunnelField.Steps); &lt;one statement&gt; }`, not inside a lambda there.
        /// </summary>
        internal static bool IsAllowedWalkStepsAccess(SyntaxNode root, int position)
        {
            SyntaxToken tok = root.FindToken(position);

            if (tok.ValueText != "_walkSteps" || tok.SpanStart != position)
            {
                return false;
            }

            if (tok.Parent is VariableDeclaratorSyntax)
            {
                return true;
            }

            if (tok.Parent is not IdentifierNameSyntax id)
            {
                return false;
            }

            if (id.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: ObjectCreationExpressionSyntax view } }
                && view.Type is IdentifierNameSyntax { Identifier.ValueText: "StepQueue" })
            {
                return true;
            }

            MethodDeclarationSyntax method = id.FirstAncestorOrSelf<MethodDeclarationSyntax>();

            return method?.Body is { Statements.Count: 2 } body
                   && Array.IndexOf(StepsMethodNames, method.Identifier.ValueText) >= 0
                   && Space.Replace(body.Statements[0].ToString(), "") == "FunnelCheck.Write(this,FunnelField.Steps);"
                   && IsQueueMutationOnThis(body.Statements[1], id);
        }

        // T1 funnel-2 (Codex audit round 5, #2): the checked statement is exactly one queue operation whose receiver is this
        // mobile's own `_walkSteps` (the bare token: not `target._walkSteps`), and it names `_walkSteps` once, so no alias or
        // callback argument lets the mutable queue escape: `_walkSteps.AddToBack(x);`, `_walkSteps.RemoveFromFront();`,
        // `_walkSteps.Clear();` or `_walkSteps[i] = x;`, with no lambda in it.
        private static bool IsQueueMutationOnThis(StatementSyntax stmt, IdentifierNameSyntax id)
        {
            if (stmt is not ExpressionStatementSyntax es
                || es.DescendantNodes().OfType<IdentifierNameSyntax>().Count(n => n.Identifier.ValueText == "_walkSteps") != 1
                || es.DescendantNodes().Any(n => n is AnonymousFunctionExpressionSyntax))
            {
                return false;
            }

            switch (es.Expression)
            {
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }:
                    return ma.Expression == id
                           && ma.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                           && Array.IndexOf(StepsQueueMutators, ma.Name.Identifier.ValueText) >= 0;

                case AssignmentExpressionSyntax { Left: ElementAccessExpressionSyntax ea } asg:
                    return asg.IsKind(SyntaxKind.SimpleAssignmentExpression) && ea.Expression == id;

                default:
                    return false;
            }
        }

        internal static readonly string[] StepsQueueMutators = { "AddToBack", "RemoveFromFront", "Clear" };

        internal static readonly string[] StepsMethodNames = { "AddStep", "RemoveFirstStep", "ReplaceStep", "ClearStepQueue" };

        /// <summary>
        /// The line ranges of the bodies of the named methods declared directly in class <paramref name="className"/> (brace
        /// matching on code without comments and strings). A method of another class or of a nested type is not included.
        /// </summary>
        internal static List<(int From, int To)> MethodBodyLines(string code, string className, string[] methods)
        {
            var result = new List<(int, int)>();
            Match cls = new Regex(@"\bclass\s+" + className + @"\b[^{]*\{").Match(code);

            if (!cls.Success)
            {
                return result;
            }

            int clsOpen = cls.Index + cls.Length - 1;
            int clsClose = MatchBrace(code, clsOpen, '{', '}');
            var decl = new Regex(@"\b(?:" + string.Join("|", methods) + @")\s*\(");

            foreach (Match m in decl.Matches(code, clsOpen))
            {
                // outside the class, or inside a member body or a nested type (a call, not a declaration of the class)
                if (m.Index > clsClose || Depth(code, clsOpen, m.Index) != 1)
                {
                    continue;
                }

                int parenClose = MatchBrace(code, m.Index + m.Length - 1, '(', ')');
                int k = parenClose + 1;

                while (k < code.Length && char.IsWhiteSpace(code[k]))
                {
                    k++;
                }

                if (k >= code.Length || code[k] != '{')
                {
                    continue;
                }

                result.Add((LineOf(code, k), LineOf(code, MatchBrace(code, k, '{', '}'))));
            }

            return result;
        }

        // the brace depth at `at`, counted from the class's opening brace (1: a class member)
        private static int Depth(string code, int open, int at)
        {
            int depth = 0;

            for (int i = open; i < at; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}') depth--;
            }

            return depth;
        }

        private static int MatchBrace(string code, int open, char o, char c)
        {
            int depth = 0;

            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == o) depth++;
                else if (code[i] == c && --depth == 0) return i;
            }

            return code.Length - 1;
        }

        // `_offset.X = v`: a member of the Offset backing field
        private static readonly Regex BareBackingSub = new Regex(@"(?<![\w\.])_offset\s*\.\s*\w+\s*(?<op>" + Op + ")", RegexOptions.Compiled);

        private static List<WriteSite> ScanSourceAll(string source, string path, HashSet<string> globalMembers, bool insideGameObjectClass)
        {
            string code = StripCommentsAndStrings(source);
            var result = new List<WriteSite>();
            HashSet<string> worldNames = CollectWorldNames(code);
            var seen = new HashSet<int>();

            foreach (Match m in Write.Matches(code))
            {
                string recv = ReceiverBefore(code, m.Index);

                if (IsWorldReceiver(recv, worldNames, globalMembers, insideGameObjectClass) && seen.Add(m.Groups["f"].Index))
                {
                    result.Add(new WriteSite(path, LineOf(code, m.Index), m.Groups["f"].Value, recv, m.Groups["op"].Value == "=" ? "assign" : "compound"));
                }
            }

            foreach (Match m in SubWrite.Matches(code))
            {
                string recv = ReceiverBefore(code, m.Index);

                if (IsWorldReceiver(recv, worldNames, globalMembers, insideGameObjectClass) && seen.Add(m.Index))
                {
                    result.Add(new WriteSite(path, LineOf(code, m.Index), "Offset", recv, "member"));
                }
            }

            foreach (Match m in Prefix.Matches(code))
            {
                string recv = Space.Replace(m.Groups["recv"].Value, string.Empty);

                // `ref item.IsMulti ? ref a : ref b`: the field is only the condition
                if (QuestionAfter.IsMatch(code, m.Index + m.Length))
                {
                    continue;
                }

                if (recv.Length > 0 && IsWorldReceiver(recv, worldNames, globalMembers, insideGameObjectClass) && seen.Add(m.Groups["f"].Index))
                {
                    result.Add(new WriteSite(path, LineOf(code, m.Index), m.Groups["f"].Value, recv, "ref/prefix"));
                }
            }

            // deconstruction: `(item.X, item.Y) = (1, 2)`, `(X, Y) = (1, 2)` inside a GameObject class
            foreach (Match m in Deconstruct.Matches(code))
            {
                int offset = m.Groups["l"].Index;

                foreach (string part in m.Groups["l"].Value.Split(','))
                {
                    Match ti = TupleItem.Match(part.Trim());
                    int at = offset + part.Length - part.TrimStart().Length;
                    offset += part.Length + 1;

                    if (!ti.Success)
                    {
                        continue;
                    }

                    string recv = ti.Groups["recv"].Success ? Space.Replace(ti.Groups["recv"].Value, string.Empty) : string.Empty;
                    bool world = recv.Length == 0 ? insideGameObjectClass : IsWorldReceiver(recv, worldNames, globalMembers, insideGameObjectClass);

                    if (world && seen.Add(at + 100000000))
                    {
                        result.Add(new WriteSite(path, LineOf(code, at), ti.Groups["f"].Value, recv.Length == 0 ? "this" : recv, "deconstruct"));
                    }
                }
            }

            // the walk queue is world state too: `recv.AddStep(...)` and the other calls that change it
            foreach (Match m in StepsCall.Matches(code))
            {
                string recv = ReceiverBefore(code, m.Index);

                if (IsWorldReceiver(recv, worldNames, globalMembers, insideGameObjectClass) && seen.Add(m.Index))
                {
                    result.Add(new WriteSite(path, LineOf(code, m.Index), "Steps", recv, "call"));
                }
            }

            // the private queue itself: only its declaration, the read-only view and the checked methods' own statement may
            // touch it, each judged by its own syntax node (T1 round 4, Codex audit round 4, #4: not by its line)
            SyntaxNode stepsRoot = null;

            foreach (Match m in StepsBackingCall.Matches(code))
            {
                stepsRoot ??= CSharpSyntaxTree.ParseText(source).GetRoot();

                if (!IsAllowedWalkStepsAccess(stepsRoot, m.Index) && seen.Add(m.Index))
                {
                    result.Add(new WriteSite(path, LineOf(code, m.Index), "Steps", "_walkSteps", "call"));
                }
            }

            if (insideGameObjectClass)
            {
                foreach (Match m in BarePrefix.Matches(code))
                {
                    if (QuestionAfter.IsMatch(code, m.Index + m.Length) || !seen.Add(m.Groups["f"].Index))
                    {
                        continue;
                    }

                    result.Add(new WriteSite(path, LineOf(code, m.Index), m.Groups["f"].Value, "this", "ref/prefix"));
                }

                foreach (Match m in BareStepsCall.Matches(code))
                {
                    if (seen.Add(m.Index))
                    {
                        result.Add(new WriteSite(path, LineOf(code, m.Index), "Steps", "this", "call"));
                    }
                }

                foreach (Match m in Bare.Matches(code))
                {
                    if (IsInitializerEntry(code, m.Groups["op"].Index + m.Groups["op"].Length, m.Index) || IsDeclaration(code, m.Index) || !seen.Add(m.Groups["f"].Index))
                    {
                        continue;
                    }

                    result.Add(new WriteSite(path, LineOf(code, m.Index), m.Groups["f"].Value, "this", "bare"));
                }

                foreach (Match m in BareSub.Matches(code))
                {
                    if (seen.Add(m.Index))
                    {
                        result.Add(new WriteSite(path, LineOf(code, m.Index), "Offset", "this", "bare"));
                    }
                }

                foreach (Match m in BareBackingSub.Matches(code))
                {
                    if (seen.Add(m.Index))
                    {
                        result.Add(new WriteSite(path, LineOf(code, m.Index), "_offset", "this", "bare"));
                    }
                }
            }

            // OffsetX/OffsetY/OffsetZ are the member-wise accessors of Offset (T0c): they count as the old `Offset.X = ` writes
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i].Field is "OffsetX" or "OffsetY" or "OffsetZ")
                {
                    result[i] = result[i] with { Field = "Offset" };
                }
            }

            result.Sort((a, b) => a.Line.CompareTo(b.Line));

            return result;
        }

        // every name the file declares with a world type (or initialises from a world producer): a name is world when ANY of its
        // declarations is, which over-counts when two methods use the same name for different types and never under-counts
        private static HashSet<string> CollectWorldNames(string code)
        {
            var names = new HashSet<string>();

            foreach (Match m in Decl.Matches(code))
            {
                string t = m.Groups["t"].Value;
                string n = m.Groups["n"].Value;
                string next = m.Groups["next"].Value;
                bool world;

                if (t == "var")
                {
                    if (next == "in")
                    {
                        int close = code.IndexOf(')', m.Groups["n"].Index);
                        string expr = close > 0 ? code.Substring(m.Groups["n"].Index, close - m.Groups["n"].Index) : string.Empty;
                        world = ForeachSource.IsMatch(expr);
                    }
                    else
                    {
                        Match init = VarInit.Match(code, m.Groups["n"].Index + n.Length);
                        world = init.Success && init.Index == m.Groups["n"].Index + n.Length
                                && (Producer.IsMatch(init.Groups["e"].Value) || NewCollection.IsMatch(init.Groups["e"].Value));
                    }
                }
                else if (Keywords.Contains(t))
                {
                    continue;
                }
                else
                {
                    world = WorldType.IsMatch(t);
                }

                if (!world)
                {
                    continue;
                }

                names.Add(n);

                // `Item hair, beard;`
                if (t != "var" && (next == "," || next == "=" || next == ";"))
                {
                    AddFollowingNames(code, m.Groups["n"].Index + n.Length, names);
                }
            }

            return names;
        }

        private static void AddFollowingNames(string code, int start, HashSet<string> names)
        {
            int depth = 0;

            for (int i = start; i < code.Length; i++)
            {
                char c = code[i];

                if (c == '(' || c == '[' || c == '{' || c == '<')
                {
                    depth++;
                }
                else if (c == ')' || c == ']' || c == '}' || c == '>')
                {
                    if (--depth < 0)
                    {
                        return;
                    }
                }
                else if (c == ';' && depth == 0)
                {
                    return;
                }
                else if (c == ',' && depth == 0)
                {
                    Match nn = NextName.Match(code, i + 1);

                    if (nn.Success)
                    {
                        names.Add(nn.Groups["n"].Value);
                    }
                }
            }
        }

        // the expression to the left of the '.' at `dot`: identifiers, dots, balanced () and []
        private static string ReceiverBefore(string code, int dot)
        {
            int i = dot - 1;

            while (i >= 0 && char.IsWhiteSpace(code[i]))
            {
                i--;
            }

            int end = i;

            while (i >= 0)
            {
                char c = code[i];

                if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?' || c == '!')
                {
                    i--;
                }
                else if (c == ')' || c == ']')
                {
                    char open = c == ')' ? '(' : '[';
                    int depth = 0;

                    for (; i >= 0; i--)
                    {
                        if (code[i] == c)
                        {
                            depth++;
                        }
                        else if (code[i] == open && --depth == 0)
                        {
                            break;
                        }
                    }

                    i--;
                }
                else if (char.IsWhiteSpace(c))
                {
                    // whitespace/newlines around a '.' in a chain, nothing else
                    int j = i;

                    while (j >= 0 && char.IsWhiteSpace(code[j]))
                    {
                        j--;
                    }

                    if ((j >= 0 && code[j] == '.') || (i + 1 <= end && code[i + 1] == '.'))
                    {
                        i = j;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            return Space.Replace(code.Substring(i + 1, end - i), string.Empty);
        }

        private static bool IsWorldReceiver(string recv, HashSet<string> worldNames, HashSet<string> globalMembers, bool insideGameObjectClass)
        {
            if (recv.Length == 0)
            {
                return false;
            }

            recv = recv.Replace("?", string.Empty).Replace("!", string.Empty);

            if (recv == "this")
            {
                return insideGameObjectClass;
            }

            // (Item)x / ((Item)x) / (x as Item)
            if (CastReceiver.IsMatch(recv))
            {
                return true;
            }

            // a call: world.Items.Get(serial), World.GetOrCreateMobile(s), item.FindItemByLayer(l)
            if (recv.EndsWith(")", StringComparison.Ordinal))
            {
                int open = MatchingOpen(recv);

                return open > 0 && GetterCall.IsMatch(recv.Substring(0, open));
            }

            // an indexer: `items[i]`, only for a world-typed array or list
            if (recv.EndsWith("]", StringComparison.Ordinal))
            {
                int open = recv.LastIndexOf('[');

                return open > 0 && IsWorldReceiver(recv.Substring(0, open), worldNames, globalMembers, insideGameObjectClass);
            }

            string[] parts = recv.Split('.');
            string last = parts[parts.Length - 1];

            if (parts.Length == 1 || parts.Length == 2 && parts[0] == "this")
            {
                return worldNames.Contains(last);
            }

            return globalMembers.Contains(last) || parts[0] == "this" && worldNames.Contains(last);
        }

        private static int MatchingOpen(string s)
        {
            int depth = 0;

            for (int i = s.Length - 1; i >= 0; i--)
            {
                if (s[i] == ')')
                {
                    depth++;
                }
                else if (s[i] == '(' && --depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        // `{ X = 1, Y = 2 }`: scanning forward from the operator, the first top-level ',' or '}' ends an initializer entry, the
        // first top-level ';' ends a statement; a ',' before the match also marks an initializer entry
        private static bool IsInitializerEntry(string code, int afterOp, int matchStart)
        {
            int p = matchStart - 1;

            while (p >= 0 && char.IsWhiteSpace(code[p]))
            {
                p--;
            }

            if (p >= 0 && code[p] == ',')
            {
                return true;
            }

            int depth = 0;

            for (int i = afterOp; i < code.Length; i++)
            {
                char c = code[i];

                if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                }
                else if (c == ')' || c == ']')
                {
                    depth--;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        return true;
                    }

                    depth--;
                }
                else if (depth == 0 && c == ',')
                {
                    return true;
                }
                else if (depth == 0 && c == ';')
                {
                    return false;
                }
            }

            return false;
        }

        // `ushort Graphic = 1;` / `Layer Layer = ...`: a declaration with an initializer, not an assignment
        private static bool IsDeclaration(string code, int start)
        {
            int p = start - 1;

            while (p >= 0 && char.IsWhiteSpace(code[p]))
            {
                p--;
            }

            if (p < 0)
            {
                return false;
            }

            char c = code[p];

            if (c == '>' || c == ']' || char.IsLetterOrDigit(c) || c == '_')
            {
                Match w = LastWord.Match(code.Substring(0, p + 1));

                return !(w.Success && (w.Groups[1].Value == "else" || w.Groups[1].Value == "do"));
            }

            return false;
        }

        private static int LineOf(string code, int index)
        {
            int line = 1;

            for (int i = 0; i < index && i < code.Length; i++)
            {
                if (code[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        /// <summary>Blanks comments, string and character literals (keeping every newline and column) so the regexes see code only.</summary>
        internal static string StripCommentsAndStrings(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;

            while (i < s.Length)
            {
                char c = s[i];
                char n = i + 1 < s.Length ? s[i + 1] : '\0';

                if (c == '/' && n == '/')
                {
                    while (i < s.Length && s[i] != '\n')
                    {
                        sb.Append(' ');
                        i++;
                    }
                }
                else if (c == '/' && n == '*')
                {
                    sb.Append("  ");
                    i += 2;

                    while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                    {
                        sb.Append(s[i] == '\n' ? '\n' : ' ');
                        i++;
                    }

                    sb.Append("  ");
                    i += 2;
                }
                else if (c == '"' || (c == '@' && (n == '"' || n == '$')) || (c == '$' && (n == '"' || n == '@')))
                {
                    // string literal: regular, verbatim, interpolated (an interpolation hole is blanked with the rest of the string)
                    bool verbatim = false;
                    int j = i;

                    while (j < s.Length && (s[j] == '@' || s[j] == '$'))
                    {
                        verbatim |= s[j] == '@';
                        j++;
                    }

                    sb.Append(' ', j - i + 1);
                    i = j + 1;

                    while (i < s.Length)
                    {
                        if (verbatim && s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"')
                        {
                            sb.Append("  ");
                            i += 2;
                        }
                        else if (!verbatim && s[i] == '\\' && i + 1 < s.Length)
                        {
                            sb.Append(s[i + 1] == '\n' ? " \n" : "  ");
                            i += 2;
                        }
                        else if (s[i] == '"')
                        {
                            sb.Append(' ');
                            i++;

                            break;
                        }
                        else
                        {
                            sb.Append(s[i] == '\n' ? '\n' : ' ');
                            i++;
                        }
                    }
                }
                else if (c == '\'')
                {
                    // char literal ('x', '\n', '\'')
                    int j = i + 1;

                    j += j < s.Length && s[j] == '\\' ? 2 : 1;

                    while (j < s.Length && s[j] != '\'' && s[j] != '\n')
                    {
                        j++;
                    }

                    for (int k = i; k <= j && k < s.Length; k++)
                    {
                        sb.Append(s[k] == '\n' ? '\n' : ' ');
                    }

                    i = j + 1;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            return sb.ToString();
        }

        internal static string FormatAllowlist(Dictionary<string, int> counts)
        {
            var sb = new StringBuilder();
            sb.Append("# Direct writes to world-state (A) fields allowed per file, counted by WriteSiteBudgetTests (regex scanner, see\n");
            sb.Append("# docs/modern-client/ecs-state-census.md, \"Scanner limits\"). Format: <file>|<field>|<count>. The count may only go down;\n");
            sb.Append("# regenerate with CUO_WRITE_BUDGET_REGEN=1 dotnet test --filter WriteSiteBudgetTests after removing writes.\n");

            foreach (KeyValuePair<string, int> kv in counts.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('|').Append(kv.Value).Append('\n');
            }

            return sb.ToString();
        }

        internal static Dictionary<string, int> ParseAllowlist(string text)
        {
            var result = new Dictionary<string, int>();

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();

                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int bar = line.LastIndexOf('|');
                result[line.Substring(0, bar)] = int.Parse(line.Substring(bar + 1));
            }

            return result;
        }
    }
}
