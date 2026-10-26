// SPDX-License-Identifier: BSD-2-Clause

using System;
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

    public struct Amount : IComponent
    {
        public ushort Value;
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
        public sbyte MountOffsetY;

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

        public EquipSlotArray Slots;
        public byte Count;

        // where the facing-dependent cloak move puts the cloak when it goes "below the helmet": the number of slots, not
        // counting the cloak, before Helmet's place in the full layer order (AnimationRules.CloakAnchor); MobileView moves
        // the cloak on the full order, so it lands there even when no helmet is drawn
        public byte CloakAnchor;

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

        private static EntitySchema CreateSchema()
        {
            NativeAOT aot = new NativeAOT();
            aot.RegisterComponent<Serial>();
            aot.RegisterComponent<Graphic>();
            aot.RegisterComponent<Hue>();
            aot.RegisterComponent<Position>();
            aot.RegisterComponent<Facing>();
            aot.RegisterComponent<Flags>();
            aot.RegisterComponent<Amount>();
            aot.RegisterIndexedComponentStruct<EquippedOn, uint>();
            aot.RegisterIndexedComponentStruct<ContainedIn, uint>();
            aot.RegisterTag<MobileTag>();
            aot.RegisterTag<ItemTag>();
            aot.RegisterTag<PlayerTag>();
            aot.RegisterTag<OnGround>();
            aot.RegisterTag<MultiTag>();
            aot.RegisterComponent<AnimationState>();
            aot.RegisterComponent<MobileRenderState>();
            aot.RegisterComponent<EquipmentSlots>();
            aot.RegisterTag<EquipmentDirty>();
            aot.RegisterComponent<MobileInputs>();

            return aot.CreateSchema();
        }
    }
}
