using System.Runtime.CompilerServices;
using ClassicUO.Game;
using GameEntity = ClassicUO.Game.GameObjects.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    /// <summary>One entity's place in a fixture's child list (see <see cref="TestChildLists"/>).</summary>
    internal sealed class ChildNode : LinkedObject
    {
        public GameEntity Owner;

        public ChildNode NextNode => (ChildNode)Next;
    }

    /// <summary>
    /// The client stopped writing the entity child list with T9 child-list slice B, and its fields left the world objects with
    /// linked-object (2026-11-07). Fixtures that link children by hand (then <c>AttachLists</c>) and the old-path references
    /// (<c>OldRemove</c>, <c>OldClear</c>, <c>LegacyRekeyReference</c> ...) still model that list: it lives here, one side node
    /// per entity, with the old API.
    /// </summary>
    internal static class TestChildLists
    {
        private static readonly ConditionalWeakTable<GameEntity, ChildNode> _nodes = new();

        internal static ChildNode Node(GameEntity e) => _nodes.GetValue(e, x => new ChildNode { Owner = x });

        extension(GameEntity e)
        {
            /// <summary>The first child's node; walk with <see cref="ChildNode.NextNode"/>.</summary>
            public ChildNode Items
            {
                get => (ChildNode)Node(e).Items;
                set => Node(e).Items = value;
            }

            public bool IsEmpty => Node(e).IsEmpty;

            public void PushToBack(GameEntity child)
            {
                if (child != null)
                {
                    Node(e).PushToBack(Node(child));
                }
            }

            public void Remove(GameEntity child)
            {
                if (child != null)
                {
                    Node(e).Remove(Node(child));
                }
            }

            public void Clear() => Node(e).Clear();
        }
    }
}
