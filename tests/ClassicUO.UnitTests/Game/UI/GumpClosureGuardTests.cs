using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // A lambda in AddToRenderLists that captures a parameter or local gets a compiler-generated display class that is
    // allocated at method entry on every call, even when the branch holding the lambda is not taken. Gumps draw every
    // frame, so such a lambda is a per-frame allocation. Queue a typed GumpCommand (SpriteAt, SpriteStretched, Tiled,
    // Sprite, Outline, Deferred through IGumpDeferredDraw) instead. A lambda capturing only `this` is fine (no display
    // class); a deliberate capturing lambda goes in Allowed with the reason.
    public class GumpClosureGuardTests
    {
        private static readonly HashSet<string> Allowed = new()
        {
            // "Declaring.Type::Method" -> reason
        };

        private static IEnumerable<MethodInfo> Overrides()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (Type type in typeof(Control).Assembly.GetTypes())
            {
                if (!typeof(Control).IsAssignableFrom(type))
                {
                    continue;
                }

                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    if (method.Name == "AddToRenderLists" && method.GetParameters().Length == 4)
                    {
                        yield return method;
                    }
                }
            }
        }

        [Fact]
        public void AddToRenderLists_has_no_capturing_lambda()
        {
            var offenders = new List<string>();

            foreach (MethodInfo method in Overrides())
            {
                MethodBody body = method.GetMethodBody();

                if (body == null)
                {
                    continue;
                }

                bool captures = body.LocalVariables.Any(l =>
                    l.LocalType.Name.Contains("DisplayClass", StringComparison.Ordinal)
                    && l.LocalType.DeclaringType == method.DeclaringType);

                string name = method.DeclaringType.FullName + "::" + method.Name;

                if (captures && !Allowed.Contains(name))
                {
                    offenders.Add(name);
                }
            }

            offenders.Sort(StringComparer.Ordinal);
            string.Join(" | ", offenders).Should().BeEmpty("a capturing lambda in AddToRenderLists allocates its closure every frame; use a typed GumpCommand");
        }
    }
}
