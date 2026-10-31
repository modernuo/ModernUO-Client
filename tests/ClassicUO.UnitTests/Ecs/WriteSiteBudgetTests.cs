using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
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
        public void Only_The_Funnel_Is_Exempt()
        {
            // the exemption exists and names the funnel alone (a second exempt file would be an unbudgeted writer)
            string root = WriteSiteScanner.FindRepoRoot();
            WriteSiteScanner.FunnelFiles.Should().BeEquivalentTo(new[] { "src/ClassicUO.Client/Game/Ecs/WorldWriter.cs" });
            File.Exists(Path.Combine(root, "src", "ClassicUO.Client", "Game", "Ecs", "WorldWriter.cs")).Should().BeTrue();
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
            "_notorietyFlag", "_inWarMode", "_direction", "_hits", "_hitsMax", "_isMulti", "_displayedGraphic"
        };

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

        // T1: the mutation funnel (ecs-transition-plan.md). Its writes are the allowed ones; the budget counts everything else.
        // Keep this to the funnel alone: a file listed here can write world state without limit.
        internal static readonly HashSet<string> FunnelFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "src/ClassicUO.Client/Game/Ecs/WorldWriter.cs"
        };

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
        private static readonly Regex StepsCall = new Regex(@"\.\s*Steps\s*\.\s*" + StepsMutators, RegexOptions.Compiled);
        private static readonly Regex BareStepsCall = new Regex(@"(?<![\w\.])Steps\s*\.\s*" + StepsMutators, RegexOptions.Compiled);
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

                // T1: the funnel is the one allowed writer, so its writes are not direct writes
                if (FunnelFiles.Contains(rel))
                {
                    continue;
                }

                string name = Path.GetFileName(path);
                bool gameObjectFile = rel.StartsWith("src/ClassicUO.Client/Game/GameObjects/", StringComparison.Ordinal) && !NotGameObjectFiles.Contains(name);
                List<WriteSite> sites = ScanSource(text, rel, globalMembers, gameObjectFile);

                if (EffectFiles.Contains(name))
                {
                    sites = sites.Where(s => s.Kind != "bare" || EffectFields.Contains(s.Field)).ToList();
                }

                result.AddRange(sites);
            }

            return result;
        }

        internal static List<WriteSite> ScanSource(string source, string path, HashSet<string> globalMembers, bool insideGameObjectClass)
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

            // the walk queue is world state too: `recv.Steps.AddToBack(...)` and the other calls that change it
            foreach (Match m in StepsCall.Matches(code))
            {
                string recv = ReceiverBefore(code, m.Index);

                if (IsWorldReceiver(recv, worldNames, globalMembers, insideGameObjectClass) && seen.Add(m.Index))
                {
                    result.Add(new WriteSite(path, LineOf(code, m.Index), "Steps", recv, "call"));
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
