using System;
using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Game.GameObjects
{
    // Heap size of one world object, header included: the bytes one allocation of the bare instance costs (no constructor runs).
    public class ObjectSizeTests
    {
        private readonly ITestOutputHelper _output;

        public ObjectSizeTests(ITestOutputHelper output) => _output = output;

        internal static long InstanceBytes(Type type)
        {
            RuntimeHelpers.GetUninitializedObject(type);
            long best = long.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                object o = RuntimeHelpers.GetUninitializedObject(type);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                GC.KeepAlive(o);
                best = Math.Min(best, bytes);
            }

            return best;
        }

        [Fact]
        public void Reports_Instance_Sizes()
        {
            foreach (Type t in new[] { typeof(Item), typeof(ClassicUO.Game.GameObjects.Mobile), typeof(Static), typeof(Land), typeof(Multi), typeof(TextObject), typeof(FixedEffect), typeof(MovingEffect) })
            {
                long bytes = InstanceBytes(t);
                _output.WriteLine($"{t.Name}: {bytes} B");
                Assert.True(bytes > 0);
            }
        }

        // linked-object (2026-11-07): the LinkedObject links (Items/Next/Previous) left BaseGameObject; only TextObject and
        // GameEffect carry list links, their own typed ones
        [Fact]
        public void World_Objects_Carry_No_LinkedObject_Fields()
        {
            Assert.False(typeof(ClassicUO.Game.LinkedObject).IsAssignableFrom(typeof(BaseGameObject)));

            foreach (Type t in new[] { typeof(Item), typeof(ClassicUO.Game.GameObjects.Mobile), typeof(PlayerMobile), typeof(Static), typeof(Land), typeof(Multi) })
            {
                for (Type b = t; b != null && b != typeof(object); b = b.BaseType)
                {
                    foreach (var f in b.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        Assert.False(f.Name is "Items" or "Next" or "Previous", $"{b.Name}.{f.Name}");
                    }
                }
            }
        }
    }
}
