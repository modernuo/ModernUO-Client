using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // G7 (gump system): MiniMapGump's draw only draws. Closing the gump, building the map and rewriting the map texture
    // happen in Update, so a per-gump recording (ui_gump_dirty, G6) can keep the minimap as a live node without the draw
    // changing state.
    public sealed class MiniMapDrawGuardTests
    {
        internal static IEnumerable<string> CalledMethods(MethodInfo method)
        {
            byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
            var names = new HashSet<string>();

            // call (0x28) and callvirt (0x6F) take a 4-byte method token; a byte that only looks like one resolves to
            // nothing or to an unrelated member, which is harmless for a deny list
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F)
                {
                    continue;
                }

                int token = BitConverter.ToInt32(il, i + 1);

                try
                {
                    MethodBase target = method.Module.ResolveMethod(token);

                    if (target != null)
                    {
                        // a control's own methods by name; others (an enumerator's Dispose) by type and name
                        names.Add(typeof(ClassicUO.Game.UI.Controls.Control).IsAssignableFrom(target.DeclaringType)
                            ? target.Name
                            : target.DeclaringType?.Name + "." + target.Name);
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return names;
        }

        [Fact]
        public void The_Draw_Has_No_Side_Effects()
        {
            MethodInfo draw = typeof(MiniMapGump)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Single(m => m.Name.EndsWith("DrawDeferred", StringComparison.Ordinal));

            var called = CalledMethods(draw).ToList();
            called.Should().Contain("UltimaBatcher2D.Draw", "the scan sees the draw's calls");
            called.Should().NotContain(new[] { "Dispose", "CreateMap", "CreateMiniMapTexture" });

            MethodInfo update = typeof(MiniMapGump).GetMethod(nameof(MiniMapGump.Update))!;
            CalledMethods(update).Should().Contain(new[] { "Dispose", "CreateMap", "CreateMiniMapTexture" });
        }
    }
}
