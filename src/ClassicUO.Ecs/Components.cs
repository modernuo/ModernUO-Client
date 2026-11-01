// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;

namespace ClassicUO.Ecs
{
    public struct Serial : IComponent
    {
        public uint Value;
    }

    // Item id (items) or body id (mobiles).
    public struct Graphic : IComponent
    {
        public ushort Value;
    }

    public struct Hue : IComponent
    {
        public ushort Value;
    }

    public struct Position : IComponent
    {
        public ushort X, Y;
        public sbyte Z;
    }

    public struct Facing : IComponent
    {
        public byte Value;
    }

    public struct Flags : IComponent
    {
        public byte Value;
    }

    // The item-only record fields, in one component every item has from its creation (T1 part 9: an item's archetype keeps
    // the width it had before -ecscheck v2, and the funnel's commit writes all three with one lookup and no archetype move):
    // - Amount;
    // - Layer: every item's (EquippedOn has it only for worn items);
    // - Stamp: its place in its parent's child list (for T2). 0 = not attached (in no parent's child list: on the ground,
    //   never linked, or linked under a parent that did not exist yet, which ClassicUO never adopts); otherwise a monotonic
    //   stamp the funnel takes at every PushToBack (arrival order; a re-push moves the child to the back), so a parent's
    //   attached children sorted by stamp are the GameObject linked list. A change of parent without a push detaches
    //   (WorldMirror.UpsertItem). 64 bits: never wraps (at a billion stamps a second, after 584 years). Friflo's index order
    //   is not stable across removes, so the order lives here.
    public struct ItemData : IComponent
    {
        public ulong Stamp;
        public ushort Amount;
        public byte Layer;
    }

    // Links are keyed by the parent's serial, not by Entity: ClassicUO accepts children before their
    // parent arrives, and a serial survives the parent being destroyed and recreated.
    public struct EquippedOn : IIndexedComponent<uint>
    {
        public uint Parent;
        public byte Layer;

        public uint GetIndexedValue() => Parent;
    }

    public struct ContainedIn : IIndexedComponent<uint>
    {
        public uint Parent;

        public uint GetIndexedValue() => Parent;
    }

    // -ecscheck v2 (T1 funnel part 9): the cold record fields the funnel commits besides the record. An entity has each
    // only once a funnel operation wrote it (absent = the GameObject's default: null name, 0 hits, Unknown notoriety, 0
    // price). The Layer and the child order are in ItemData, which every item has from its creation.
    public struct EntityName : IComponent
    {
        public string Value;
    }

    // T4: the stamina and mana pools share the component with the hits (one archetype column, added on the first non-zero
    // write of any of the six; absent = all 0, the GameObject default).
    public struct Vitals : IComponent
    {
        public ushort Hits, HitsMax;
        public ushort Stamina, StaminaMax, Mana, ManaMax;
    }

    public struct MobileNotoriety : IComponent
    {
        public byte Value;
    }

    public struct ItemPrice : IComponent
    {
        public uint Value;
    }

    // T2: the spells a spellbook's 0xBF.1B content lists (bit i = spell i + 1). ClassicUO keeps them as detached entries at
    // the front of the book's child list (not world objects, so no records); the mirror keeps the set. Added on the first
    // non-empty content (absent = none).
    public struct SpellbookContent : IComponent
    {
        public ulong Spells;
    }

    [InlineArray(WornLayers.COUNT)]
    public struct WornLayerArray
    {
        private uint _e;
    }

    [InlineArray(WornLayers.COUNT)]
    public struct WornCountArray
    {
        private byte _e;
    }

    /// <summary>
    /// T3a: a mobile's worn equipment by layer (layers 0x01..0x1D; slot <c>layer - 1</c>): the serial of the first item
    /// attached to the mobile on that layer, i.e. the one <c>Entity.FindItemByLayer</c> finds first in the mobile's child list
    /// (the lowest child-order stamp among the attached items on that layer), 0 when none. Maintained by the mirror from
    /// attachment only (<see cref="ItemData.Stamp"/> != 0): an item equipped before its mobile existed (an orphan, stamp 0)
    /// is never in it, as ClassicUO never links it. Layer 0 (the trade boxes) and layers above 0x1D are not tracked.
    /// Held in <see cref="EquipmentSlots.Worn"/>, not as a component of its own: an 11th component on the mobile archetype
    /// made Friflo's delete + create of a mobile record 6x slower (0.40 us against 0.06 us for ten, populated archetypes),
    /// and the E2 serial-reuse row +60 %.
    /// </summary>
    public struct WornLayers
    {
        /// <summary>The tracked layers: 0x01 (OneHanded) .. 0x1D (Bank).</summary>
        public const int COUNT = 0x1D;

        public WornLayerArray Serials;

        /// <summary>Per layer, the attached items on it (the first is in <see cref="Serials"/>): a departure rescans the
        /// wearer's equipment only when another one is left.</summary>
        public WornCountArray Counts;

        /// <summary>
        /// T3b: changes with every write the mirror sees to the wearer's equipment (<c>MarkEquipmentDirty</c>: a worn item's
        /// graphic or hue, an equip or unequip, the body), a superset of <see cref="Version"/>'s slot changes: a worn item that
        /// changes its graphic in place keeps its slot. A per-record counter (it sits in the padding before Version, so
        /// WornLayers stays 160 B); LayerDecision keys on it with Version and the mirror-wide <c>EquipChanges</c>.
        /// </summary>
        public uint Look;

        /// <summary>Changes with every slot change (a mirror-wide 64-bit counter, never reused): readers' caches key on it.</summary>
        public ulong Version;

        /// <summary>A layer's count at 255 means 255 or more (it saturates, never wraps): a departure rescans the wearer's
        /// equipment for the exact count and the first item.</summary>
        public const byte SATURATED = byte.MaxValue;

        /// <summary>The first attached item's serial on <paramref name="layer"/>, 0 when none or the layer is not tracked.</summary>
        public readonly uint Get(byte layer) => (uint)(layer - 1) < COUNT ? Serials[layer - 1] : 0;

        public static bool Tracked(byte layer) => (uint)(layer - 1) < COUNT;
    }

    public struct MobileTag : ITag { }

    public struct ItemTag : ITag { }

    public struct PlayerTag : ITag { }

    // Item lying in the world: not equipped, not in a container.
    public struct OnGround : ITag { }

    public struct MultiTag : ITag { }

    [Flags]
    public enum AnimStateFlags : uint
    {
        None = 0,

        // selection inputs, sampled from the legacy Mobile until the ECS owns the world update (W5)
        FromServer = 1 << 0,
        WarMode = 1 << 1,
        Walking = 1 << 2,
        Running = 1 << 3,
        HasSteps = 1 << 4,
        StepRun = 1 << 5,
        Mounted = 1 << 6,
        Flying = 1 << 7,
        Dead = 1 << 8,
        Hidden = 1 << 9,
        Female = 1 << 10,
        Human = 1 << 11,

        // Mobile.Draw's gargoyle test (CV_7000+, bodies 666/667/0x2B6/0x2B7) and Mobile.IsGargoyle (with its precedence
        // quirk: CV_7000+ and 0x29A, or 0x29B on any version) differ; both are kept
        GargoyleBody = 1 << 12,
        GargoyleProp = 1 << 13,

        // persistent: GetAnimDirection leaves the mirror untouched for directions 3 and 7, so it carries over frames
        Mirror = 1 << 14,

        // selection results
        Visible = 1 << 15,
        Shadow = 1 << 16,
        MountDrawn = 1 << 17,
        Sitting = 1 << 18,

        // seated with the human sitting pose: drawn as one sprite, not in strips
        CharSitting = 1 << 19
    }

    /// <summary>
    /// A mobile's animation: the inputs of ClassicUO's action selection and the selected body/mount action, stored
    /// direction, frame and seat offsets. Written by AnimationStateSystem; read by the sprite extract.
    /// </summary>
    public struct AnimationState : IComponent
    {
        public AnimStateFlags Flags;

        // GetGraphicForAnimation: the body the animation is looked up for
        public ushort Body;

        // server-requested action (0xFF = none)
        public byte ServerGroup;

        // raw facing 0..7 (the front step's direction while walking): the equipment order key
        public byte Facing;

        public byte Frame;
        public byte Group;

        // stored direction 0..4 after GetAnimDirection (and the sitting remap)
        public byte Dir;

        public byte MountGroup;
        public ushort MountBody;
        // Mounts table offset + pack MountShift (-256..254: both sbytes, summed without clamping)
        public short MountOffsetY;

        // seat offsets (FixSittingDirection + SIT_OFFSET_Y, gargoyle lift) added to the draw position
        public short SitX, SitY;

        // Mobile.LastAnimationChangeTime: when the frame advances next
        public uint NextFrameTime;
    }

    [Flags]
    public enum MobileHueBits : byte
    {
        None = 0,
        Poisoned = 1,
        Paralyzed = 2,
        YellowHits = 4,
        Invulnerable = 8
    }

    /// <summary>
    /// W5 (setting mobile_push_inputs): the selection and override-hue inputs of a mobile that change only on a server
    /// update, a step or a client-side state change. The client's change funnel (WorldEcs.Sync / WorldEcs.PushInputs,
    /// called from every mutation site of these values) recomputes it, so the per-frame sample doesn't read them from the
    /// legacy Mobile. Whether the mobile is mounted comes from <see cref="EquipmentSlots.Mounted"/> (rebuilt on
    /// <see cref="EquipmentDirty"/>), not from here.
    /// </summary>
    public struct MobileInputs : IComponent
    {
        // only the pushed bits: WarMode, Running, Flying, Dead, Hidden, Female, Human, GargoyleBody, GargoyleProp,
        // HasSteps, StepRun (MobileInputRules.PUSHED)
        public AnimStateFlags Flags;

        // GetGraphicForAnimation
        public ushort Body;

        // ProcessSteps' direction: the front step's (masked to 0..7 when it runs), else Direction & 7
        public byte Facing;

        // NotorietyFlag; its hue is looked up per frame (the profile's notoriety colours can change)
        public byte Notoriety;
        public MobileHueBits HueBits;

        // IsWalking = LastStepTime > Ticks - WalkingDelay (uint arithmetic, as the Mobile computes it)
        public uint LastStepTime;
        public uint WalkingDelay;

        // pushes since the entity was created (diagnostics)
        public uint Pushes;
    }

    [Flags]
    public enum MobileClockFlags : byte
    {
        None = 0,

        // the mobile has queued steps: ProcessSteps runs every frame
        Stepping = 1,

        // ObjectHandlesStatus is OPEN: Entity.Update opens the name gump on the next update
        HandlesOpening = 2
    }

    /// <summary>
    /// E4 (setting world_update_mobile_system): when a mobile's <c>Mobile.Update</c> has something to do. The legacy Mobile
    /// stays authoritative for the animation timer (interval, repeat state, group, frame) and writes the two due times
    /// here whenever it changes them; <see cref="MobileClockSystem"/> reads only this component to pick the mobiles whose
    /// Update is not a no-op this frame.
    /// </summary>
    public struct MobileClock : IComponent
    {
        // Mobile.LastAnimationChangeTime: ProcessAnimation advances the frame only when this is before the tick
        public long AnimDue;

        // Mobile._lastAnimationIdleDelay: SetIdleAnimation runs only when this is before the tick
        public long IdleDue;

        public MobileClockFlags Flags;
    }

    /// <summary>The due-time rule of <see cref="MobileClock"/>, the single definition both the system and the checker use.</summary>
    public static class MobileClockRules
    {
        /// <summary>
        /// True when Mobile.Update can do anything at <paramref name="now"/>: queued steps, a pending handles opening, the
        /// frame timer expired (<c>LastAnimationChangeTime &lt; Ticks</c>) or the idle timer expired. Conservative by
        /// construction: Update's other gates (ExecuteAnimation, the recent-step gate) are evaluated by Update itself.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsDue(in MobileClock c, long now)
        {
            return c.Flags != MobileClockFlags.None || c.AnimDue < now || c.IdleDue < now;
        }
    }

    /// <summary>
    /// T5b (world_anim_system): a mobile's animation timer, what <c>Mobile.Update</c>'s idle animation and frame advance read
    /// and write. The first eight fields are the frame-advance state <c>AnimationTiming.Advance</c> steps (Mobile.AnimIndex,
    /// AnimationFrameCount, the interval, the repeat mode and count, repeat, forward, AnimationFromServer); then the animation
    /// group (the server's action, 0xFF = none), <c>ExecuteAnimation</c> and the two due times (<c>LastAnimationChangeTime</c>
    /// and the idle delay). While the system runs, every write of these on the Mobile reaches the component with the clock
    /// push (<c>WorldEcs.WriteClock</c>), and <c>MobileAnimationAdvanceSystem</c> reads only it and the pushed inputs for a
    /// due mobile without steps.
    /// </summary>
    public struct AnimationTimer : IComponent
    {
        public byte Frame;
        public byte FrameCount;
        public byte Interval;
        public ushort RepeatMode;
        public ushort RepeatModeCount;
        public bool Repeat;
        public bool Forward;
        public bool FromServer;

        public byte Group;
        public bool ExecuteAnimation;
        public long AnimDue;
        public long IdleDue;
    }

    /// <summary>T6c: one queued step of a mobile (<c>Mobile.Step</c>): the tile it ends on, its direction and run flag.</summary>
    public struct StepEntry
    {
        public int X, Y;
        public sbyte Z;
        public byte Direction;
        public bool Run;
    }

    [InlineArray(Steps.CAPACITY)]
    public struct StepRing
    {
        private StepEntry _e;
    }

    /// <summary>
    /// T6c (world_step_system): a mobile's walk queue and the step state <c>StepSystem</c> reads: a fixed ring of up to
    /// <see cref="CAPACITY"/> steps (<c>Constants.MAX_STEP_COUNT</c> - 1 + 3; the front step is <c>Ring[Head]</c>), the time the
    /// current step started (<c>Mobile.LastStepTime</c>) and the interpolated walk offset (<c>Mobile.Offset</c>). While the
    /// system runs, the clock push writes it from the Mobile after every queue change and step completion (the same push
    /// that keeps <see cref="MobileClock"/>'s Stepping flag), and the system writes the offset it interpolates. The Mobile's
    /// queue and offset stay a write-through cache: views read them every frame.
    /// </summary>
    public struct Steps : IComponent
    {
        // the legacy queue holds at most 7: Mobile.PlanStep accepts a packet while Count < Constants.MAX_STEP_COUNT (5), so up to
        // 4 queued + the 3 steps one packet plans (a turn, the move, the final facing). A test asserts it from the client's constant.
        public const int CAPACITY = 7;

        public StepRing Ring;
        public byte Head, Count;
        public uint LastStepTime;
        public float OffsetX, OffsetY, OffsetZ;
    }

    /// <summary>Per-frame view context of a mobile: overriding hue (highlight, notoriety, out of range...), alpha, step offset.</summary>
    public struct MobileRenderState : IComponent
    {
        public ushort OverrideHue;
        public byte Alpha;

        // (int)Offset.X and (int)(Offset.Y - Offset.Z) of the walking step
        public short OffsetX, OffsetY;
    }

    [Flags]
    public enum EquipSlotBits : byte
    {
        None = 0,
        PartialHue = 1,
        Light = 2,
        Converted = 4
    }

    public struct EquipSlot
    {
        public uint Item;
        public ushort Graphic;

        // tiledata AnimID of the item, and the body actually drawn (gargoyle remap, equipconv.def)
        public ushort AnimId;
        public ushort DrawBody;
        public ushort Hue;

        // equipconv.def colour, used when neither the item nor the body file gives a hue
        public ushort ConvColor;
        public byte Layer;
        public EquipSlotBits Bits;

        // per-frame action of this layer: gargoyles select it per equipment body; 0xFF = the body's action
        public byte Group;
    }

    // what the action selection reads of a one- or two-handed item
    public struct HandItem
    {
        public bool Present, IsLight;
        public ushort AnimId;
    }

    [InlineArray(EquipmentSlots.MAX)]
    public struct EquipSlotArray
    {
        private EquipSlot _e;
    }

    [InlineArray(EquipmentSlots.MAX_LIGHT_ONLY)]
    public struct LightItemArray
    {
        private uint _e;
    }

    /// <summary>
    /// A mobile's drawn equipment, compacted and in paint order (PaperdollOrder table and reorder rules; covered layers,
    /// light-only items and a dead mobile's hair removed). Rebuilt only when equipment changes (<see cref="EquipmentDirty"/>);
    /// the facing-dependent cloak move is applied by the extract. Also carries the mount and the hand items that the
    /// action selection reads.
    /// </summary>
    public struct EquipmentSlots : IComponent
    {
        public const int MAX = 25;

        /// <summary>T3a: the worn items by layer, maintained by the mirror (kept across the slots' rebuilds).</summary>
        public WornLayers Worn;

        public EquipSlotArray Slots;
        public byte Count;

        // where the facing-dependent cloak move puts the cloak when it goes "below the helmet": the number of slots, not
        // counting the cloak, before Helmet's place in the full layer order (AnimationRules.CloakAnchor); MobileView moves
        // the cloak on the full order, so it lands there even when no helmet is drawn
        public byte CloakAnchor;

        // T3b, ecs_layer_rules: the interned per-facing paint orders from the data's direction rules (EquipDirOrders);
        // 0 = the hard-coded cloak move with CloakAnchor (always 0 with the switch off)
        public ushort DirOrder;

        public uint MountItem;
        public ushort MountGraphic, MountAnimId, MountHue;

        // Mobile.IsMounted: a mount item that isn't a boat's tiller (0x3E96) and whose mount body isn't 0xFFFF (W5)
        public bool Mounted;

        public HandItem Hand1, Hand2;

        // Light items the view doesn't draw but lights at the body's anchor (MobileView: a human's light without an equip
        // AnimID, any light on a non-human body), in paint order; serials. Drawn lights carry EquipSlotBits.Light.
        public const int MAX_LIGHT_ONLY = 4;
        public LightItemArray LightItems;
        public byte LightCount;

        // set on every rebuild from a counter shared by all mobiles, so a version never repeats (W5's MobileFrameCache
        // keys on it, also across a mirror entity being re-created); 0 = never built
        public uint Version;
    }

    // Equipment (or the body graphic or sex) changed: EquipmentSystem rebuilds EquipmentSlots before the next extract.
    public struct EquipmentDirty : ITag { }

    // T5d: the item is in the world's per-frame update list (corpses, items whose object handles just opened); present only
    // while world_active_items_system is on. The list order is the visiting order; the tag is the ECS-side membership.
    public struct ItemActive : ITag { }

    public static class EcsSchema
    {
        private static readonly object _lock = new object();
        private static EntitySchema _schema;

        // NativeAOT has no reflection-based type discovery: register every component and tag before any EntityStore exists.
        public static void Register()
        {
            lock (_lock)
            {
                if (_schema == null)
                {
                    _schema = CreateSchema();
                }
            }
        }

        private static readonly List<Type> _registered = new List<Type>();

        /// <summary>
        /// Every component and tag type the schema registers (tests: each IComponent/ITag type of this assembly must be here;
        /// an unregistered one throws "IComponent type not found" at its first use in a client, bench-000095).
        /// </summary>
        public static IReadOnlyList<Type> RegisteredTypes
        {
            get
            {
                Register();

                return _registered;
            }
        }

        private static EntitySchema CreateSchema()
        {
            NativeAOT aot = new NativeAOT();
            Component<Serial>(aot);
            Component<Graphic>(aot);
            Component<Hue>(aot);
            Component<Position>(aot);
            Component<Facing>(aot);
            Component<Flags>(aot);
            Component<ItemData>(aot);
            aot.RegisterIndexedComponentStruct<EquippedOn, uint>();
            _registered.Add(typeof(EquippedOn));
            aot.RegisterIndexedComponentStruct<ContainedIn, uint>();
            _registered.Add(typeof(ContainedIn));
            Tag<MobileTag>(aot);
            Tag<ItemTag>(aot);
            Tag<PlayerTag>(aot);
            Tag<OnGround>(aot);
            Tag<MultiTag>(aot);
            Component<AnimationState>(aot);
            Component<MobileRenderState>(aot);
            Component<EquipmentSlots>(aot);
            Tag<EquipmentDirty>(aot);
            Tag<ItemActive>(aot); // T5d
            Component<MobileInputs>(aot);
            Component<MobileClock>(aot);
            Component<AnimationTimer>(aot); // T5b
            Component<Steps>(aot); // T6c

            // -ecscheck v2 (T1 funnel part 9)
            Component<EntityName>(aot);
            Component<Vitals>(aot);
            Component<MobileNotoriety>(aot);
            Component<ItemPrice>(aot);

            // T2: container contents
            Component<SpellbookContent>(aot);


            return aot.CreateSchema();
        }

        private static void Component<T>(NativeAOT aot) where T : struct, IComponent
        {
            aot.RegisterComponent<T>();
            _registered.Add(typeof(T));
        }

        private static void Tag<T>(NativeAOT aot) where T : struct, ITag
        {
            aot.RegisterTag<T>();
            _registered.Add(typeof(T));
        }
    }
}
