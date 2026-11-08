// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Logging;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Position = ClassicUO.Ecs.Position;
using Entity = ClassicUO.Game.GameObjects.Entity;

namespace ClassicUO.Game
{
    /// <summary><c>BoatMovingManager.Update</c>'s decision for a boat's front step this frame.</summary>
    internal struct BoatStepDecision
    {
        // the offset toward the front step is written (InterpolateBoat: OffsetZ, then OffsetX and OffsetY)
        public bool Interpolate;
        public sbyte X, Y, Z;

        // the front step is done (CompleteBoatStep); a turn in place when DirectionChange (the next step follows this frame)
        public bool Remove;
        public bool DirectionChange;

        public bool Equals(in BoatStepDecision o) =>
            Interpolate == o.Interpolate && X == o.X && Y == o.Y && Z == o.Z && Remove == o.Remove && DirectionChange == o.DirectionChange;

        public override string ToString() => $"interp {Interpolate} ({X},{Y},{Z}) remove {Remove} turn {DirectionChange}";
    }

    /// <summary>
    /// <c>BoatMovingManager</c>'s step timing and offset math, unchanged, shared by its dictionaries (<c>ecs_boat_steps</c> off,
    /// and the checker's shadow) and <see cref="BoatStepSystem"/>; plus the <see cref="BoatSteps"/> ring and passenger list.
    /// </summary>
    internal static class BoatStepRules
    {
        private const int SLOW_INTERVAL = 1000;
        private const int NORMAL_INTERVAL = 500;
        private const int FAST_INTERVAL = 250;

        /// <summary>The time (ms) a step takes at the server's boat speed.</summary>
        public static int Velocity(byte speed)
        {
            switch (speed)
            {
                case 0x02: return SLOW_INTERVAL;

                default:
                case 0x03: return NORMAL_INTERVAL;

                case 0x04: return FAST_INTERVAL;

                case > 0x04: return speed * 10;
            }
        }

        /// <summary>The front step <paramref name="step"/> of a boat at <paramref name="x"/>,<paramref name="y"/>,<paramref name="z"/>
        /// whose current step started at <paramref name="lastStepTime"/>.</summary>
        public static void Decide(in BoatStepEntry step, int x, int y, sbyte z, uint lastStepTime, uint ticks, out BoatStepDecision d)
        {
            d = default;
            int maxDelay = step.TimeDiff;
            int delay = (int)ticks - (int)lastStepTime;
            bool removeStep = delay >= maxDelay;
            bool directionChange = false;

            if (x != step.X || y != step.Y)
            {
                if (maxDelay != 0)
                {
                    float steps = maxDelay / (float)Constants.CHARACTER_ANIMATION_DELAY;
                    float fx = delay / (float)Constants.CHARACTER_ANIMATION_DELAY;
                    float fy = fx;
                    sbyte offsetZ = (sbyte)((step.Z - z) * fx * (4.0f / steps));
                    MovementSpeed.GetPixelOffset(step.Direction, ref fx, ref fy, steps);

                    d.Interpolate = true;
                    d.X = (sbyte)fx;
                    d.Y = (sbyte)fy;
                    d.Z = offsetZ;
                }
            }
            else
            {
                directionChange = true;
                removeStep = true;
            }

            d.Remove = removeStep;
            d.DirectionChange = directionChange;
        }

        public static ref BoatStepEntry Front(ref BoatSteps s) => ref s.Ring[s.Head];

        public static ref BoatStepEntry At(ref BoatSteps s, int k) => ref s.Ring[(s.Head + k) % BoatSteps.CAPACITY];

        public static void PopFront(ref BoatSteps s)
        {
            s.Ring[s.Head] = default;
            s.Head = (byte)((s.Head + 1) % BoatSteps.CAPACITY);
            s.Count--;
        }

        public static void PushBack(ref BoatSteps s, in BoatStepEntry step)
        {
            s.Ring[(s.Head + s.Count) % BoatSteps.CAPACITY] = step;
            s.Count++;
        }

        /// <summary><c>BoatMovingManager.PushItemToList</c>: a listed object's offset is updated, a new one appended (the scan
        /// stops at an invalid serial, as the old list's did).</summary>
        public static void SetPassenger(ref BoatSteps s, uint serial, int x, int y, int z)
        {
            BoatPassenger[] a = s.Passengers;

            for (int i = 0; i < s.PassengerCount; i++)
            {
                ref BoatPassenger p = ref a[i];

                if (!SerialHelper.IsValid(p.Serial))
                {
                    break;
                }

                if (p.Serial == serial)
                {
                    p.X = x;
                    p.Y = y;
                    p.Z = z;

                    return;
                }
            }

            if (a == null || s.PassengerCount == a.Length)
            {
                Array.Resize(ref s.Passengers, Math.Max(4, s.PassengerCount * 2));
                a = s.Passengers;
            }

            a[s.PassengerCount++] = new BoatPassenger { Serial = serial, X = x, Y = y, Z = z };
        }
    }

    /// <summary>
    /// <c>ecs_boat_steps</c>: <c>BoatMovingManager.Update</c> as a system over the boats' <see cref="BoatSteps"/> and their
    /// records' <see cref="Position"/>. Once per frame (GameScene.Update, where the manager's Update ran): every boat with a
    /// queued step, in the old manager's order, decides its front step with <see cref="BoatStepRules.Decide"/> and writes
    /// through the same boat operations as before (<c>InterpolateBoat</c>, <c>FollowBoatComponents</c>, <c>FollowBoat</c>,
    /// <c>CompleteBoatStep</c>, <c>MoveBoatPassenger</c>, <c>SetBoatStepTime</c>) with the same values; the system itself
    /// only advances the ring. The boats are collected first (the operations can move a boat's record between archetypes:
    /// ItemOffset, OnGround).
    /// CUO_BOAT_CHECK=1 keeps the manager's dictionaries in shadow: at every decision the shadow's front step and decision
    /// from the Item's fields are compared with the component's, and after every boat its queue, step clock and passenger
    /// list.
    /// </summary>
    internal sealed class BoatStepSystem
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_BOAT_CHECK=1, the manager's dictionaries in shadow, compared every frame
        public static readonly bool EnvCheck = Environment.GetEnvironmentVariable("CUO_BOAT_CHECK") == "1";
#else
        public static readonly bool EnvCheck = false;
#endif

        /// <summary>Tests: the checker on or off (null: the environment).</summary>
        internal static bool? CheckOverride;

        public static bool Check => CheckOverride ?? EnvCheck;

        public static long Frames, Boats, Interpolated, Completed, Turns, PassengerMoves, Follows, Fallbacks;
        public static long Checks, StateChecks, DecisionDiffs, StateDiffs;
        private static int _logged;
        private static uint _logAt;

        /// <summary>Tests: the counters back to zero.</summary>
        internal static void ResetCounters()
        {
            Frames = Boats = Interpolated = Completed = Turns = PassengerMoves = Follows = Fallbacks = 0;
            Checks = StateChecks = DecisionDiffs = StateDiffs = 0;
            _logged = 0;
            _logAt = 0;
        }

        private readonly World _world;

        // the boats in the old manager's order (Codex audit P2): its _steps dictionary's keys, added at a boat's first queued
        // step and removed when a frame finds the boat's Item gone. One boat's step can move another (a boat listed as a
        // passenger), so the order is part of the result. The same Dictionary with the same adds and removals enumerates in
        // the same order: insertion order, a freed slot reused by the next add (the last freed first).
        // The value is a boat's pending state while it has no record (Codex audit P2): the old dictionaries were keyed by
        // serial, so a boat removed with steps queued kept its queue, passengers and clock until the next frame dropped them,
        // and a boat recreated under the serial before that frame took them over (its multi reload's ClearSteps reset the
        // passengers' offsets from them). The record's BoatSteps is parked here when the record is removed (Park) and goes
        // to the new record at its next touch (Restore), is consumed by ClearSteps (TakeParked), or is dropped with the key
        // by the next frame that finds no Item under the serial: at most one frame for a boat that does not come back.
        // A boat removed with nothing queued parks nothing; its key stays, as the old _steps key did.
        private readonly Dictionary<uint, BoatSteps> _order = new Dictionary<uint, BoatSteps>();
        private readonly List<uint> _due = new List<uint>(), _stepped = new List<uint>(), _gone = new List<uint>();

        public BoatStepSystem(World world)
        {
            _world = world;
        }

        /// <summary>The boat's record when it carries a <see cref="BoatSteps"/>.</summary>
        internal static bool TryBoatSteps(Item boat, out EcsEntity e) => boat.TryScalarEntity(out e) && e.HasComponent<BoatSteps>();

        /// <summary>A step is queued on <paramref name="serial"/> (<c>WorldWriter.QueueBoatStep</c>): the boat joins the
        /// stepping order unless it is in it (the old <c>AddStep</c>'s <c>_steps[serial]</c>).</summary>
        internal void Track(uint serial) => _order.TryAdd(serial, default);

        /// <summary>Tests: the boats whose pending state waits without a record.</summary>
        internal int ParkedCount
        {
            get
            {
                int n = 0;

                foreach (BoatSteps s in _order.Values)
                {
                    n += s.Count != 0 ? 1 : 0;
                }

                return n;
            }
        }

        /// <summary>The boat's record is being removed (<c>WorldEcs.OnDestroyed</c>): its queued steps, passengers and clock
        /// wait under its serial (see <see cref="_order"/>).</summary>
        // Codex re-review P3 (accepted): a boat with an empty queue but listed passengers keeps no parked state, where the old
        // manager kept _items[serial]. Every smooth 0xF6 resends the full passenger list before the next step uses it, so only
        // CUO_BOAT_CHECK can see the difference (a passenger-state mismatch on the next frame).
        internal void Park(uint serial, in BoatSteps steps)
        {
            ref BoatSteps slot = ref CollectionsMarshal.GetValueRefOrNullRef(_order, serial);

            if (steps.Count != 0 && !Unsafe.IsNullRef(ref slot))
            {
                slot = steps;
            }
        }

        /// <summary>A boat recreated under a parked serial (its record <paramref name="e"/>) takes the parked state over, with
        /// the new Item's step clock (the old Update read the Item's <c>LastStepTime</c>). Called by the boat operations and
        /// the frame before they read the component.</summary>
        internal void Restore(Item boat, EcsEntity e)
        {
            ref BoatSteps slot = ref CollectionsMarshal.GetValueRefOrNullRef(_order, boat.Serial);

            if (Unsafe.IsNullRef(ref slot) || slot.Count == 0)
            {
                return;
            }

            BoatSteps s = slot;
            slot = default;
            s.LastStepTime = boat.LastStepTime;

            if (e.HasComponent<BoatSteps>())
            {
                e.GetComponent<BoatSteps>() = s;
            }
            else
            {
                e.AddComponent(s);
            }
        }

        /// <summary><c>ClearSteps</c> for a boat without a record: the parked state, taken out (the key stays).</summary>
        internal bool TakeParked(uint serial, out BoatSteps steps)
        {
            ref BoatSteps slot = ref CollectionsMarshal.GetValueRefOrNullRef(_order, serial);

            if (Unsafe.IsNullRef(ref slot) || slot.Count == 0)
            {
                steps = default;

                return false;
            }

            steps = slot;
            slot = default;

            return true;
        }

        /// <summary>The parked state, read only (the dev trace).</summary>
        internal bool TryParked(uint serial, out BoatSteps steps)
        {
            steps = _order.TryGetValue(serial, out BoatSteps s) ? s : default;

            return steps.Count != 0;
        }

        /// <summary><c>ClearEntities</c> for a boat without an Item: the parked passengers dropped (the old <c>_items.Remove</c>).</summary>
        internal void DropParkedPassengers(uint serial)
        {
            ref BoatSteps slot = ref CollectionsMarshal.GetValueRefOrNullRef(_order, serial);

            if (!Unsafe.IsNullRef(ref slot))
            {
                slot.PassengerCount = 0;
            }
        }

        /// <summary>One frame of every boat with a queued step, in the old manager's order; <paramref name="shadow"/> (the
        /// checker) follows each step.</summary>
        public void Run(BoatMovingManager shadow)
        {
            Frames++;
            _due.Clear();
            _stepped.Clear();

            // collected first: the operations can move a boat's record between archetypes, and the removals wait for the end
            foreach (uint serial in _order.Keys)
            {
                _due.Add(serial);
            }

            for (int i = 0; i < _due.Count; i++)
            {
                uint serial = _due[i];
                Item item = _world.Items.Get(serial);

                if (item == null || item.IsDestroyed)
                {
                    // the old Update dropped a gone boat only with steps queued (its record's, parked at the removal)
                    if (TryParked(serial, out _))
                    {
                        _gone.Add(serial);
                    }

                    continue;
                }

                if (item.TryScalarEntity(out EcsEntity e))
                {
                    Restore(item, e);
                }

                if (!TryBoatSteps(item, out e) || e.GetComponent<BoatSteps>().Count == 0)
                {
                    continue;
                }

                _stepped.Add(serial);
                Boats++;
                Step(item, shadow);
            }

            // the old Update's _toRemove, in its order
            for (int i = 0; i < _gone.Count; i++)
            {
                _order.Remove(_gone[i]);
            }

            _gone.Clear();

            if (shadow != null)
            {
                shadow.ShadowFrameEnd(_stepped);
            }
        }

        private void Step(Item item, BoatMovingManager shadow)
        {
            uint serial = item.Serial;

            while (TryBoatSteps(item, out EcsEntity e))
            {
                ref BoatSteps s = ref e.GetComponent<BoatSteps>();

                if (s.Count == 0)
                {
                    break;
                }

                // copies: the operations below can move the record to another archetype, which invalidates s
                BoatStepEntry step = BoatStepRules.Front(ref s);
                uint last = s.LastStepTime;
                BoatPassenger[] passengers = s.Passengers;
                int passengerCount = s.PassengerCount;
                int x, y;
                sbyte z;

                if (EcsRead.TryGet(e, out Position p))
                {
                    x = p.X;
                    y = p.Y;
                    z = p.Z;
                }
                else
                {
                    x = item.X;
                    y = item.Y;
                    z = item.Z;
                    Fallbacks++;
                }

                BoatStepRules.Decide(in step, x, y, z, last, Time.Ticks, out BoatStepDecision d);

                if (shadow != null)
                {
                    shadow.ShadowDecide(item, in step, in d);
                }

                if (d.Interpolate)
                {
                    _world.Writer.InterpolateBoat(item, d.X, d.Y, d.Z);
                    Interpolated++;
                }

                _world.HouseManager.TryGetHouse(item, out House house);

                if (d.Remove)
                {
                    _world.Writer.CompleteBoatStep(item, step.X, step.Y, step.Z);

                    if (TryBoatSteps(item, out e))
                    {
                        BoatStepRules.PopFront(ref e.GetComponent<BoatSteps>());
                    }

                    shadow?.ShadowPop(serial);
                    Completed++;

                    house?.Generate(true, true, true);

                    for (int k = 0; k < passengerCount; k++)
                    {
                        ref BoatPassenger it = ref passengers[k];
                        Entity entity = _world.Get(it.Serial);

                        if (entity == null || entity.IsDestroyed)
                        {
                            continue;
                        }

                        _world.Writer.MoveBoatPassenger(entity, (ushort)(step.X - it.X), (ushort)(step.Y - it.Y), (sbyte)(step.Z - it.Z));
                        PassengerMoves++;
                    }

                    _world.Writer.SetBoatStepTime(item, Time.Ticks);
                }
                else
                {
                    if (house != null)
                    {
                        _world.Writer.FollowBoatComponents(item, house);
                    }

                    if (passengerCount != 0)
                    {
                        // the boat's offset as the item readers see it (ecs_item_offset: the ItemOffset component)
                        Vector3 offset = ItemOffsets.Read(item);

                        for (int k = 0; k < passengerCount; k++)
                        {
                            Entity entity = _world.Get(passengers[k].Serial);

                            if (entity == null || entity.IsDestroyed)
                            {
                                continue;
                            }

                            _world.Writer.FollowBoat(entity, offset);
                            Follows++;
                        }
                    }
                }

                if (!d.DirectionChange)
                {
                    break;
                }

                Turns++;
            }
        }

        /// <summary>The checker's finding (a warning for the first ten).</summary>
        internal static void Diff(bool decision, string text)
        {
            if (decision)
            {
                DecisionDiffs++;
            }
            else
            {
                StateDiffs++;
            }

            if (_logged++ < 10)
            {
                Log.Warn($"[boat-check] {text} (tick {Time.Ticks})");
            }
        }

        /// <summary>The counters, for the periodic log line.</summary>
        internal static string Summary(World world) =>
            $"[boat-check] ecs_boat_steps {(world.BoatStepsOn ? "on" : "off")}: frames {Frames}, boats stepped {Boats}, interpolated {Interpolated}, " +
            $"steps done {Completed} (turns {Turns}), passenger moves {PassengerMoves}, follows {Follows}, position fallbacks {Fallbacks}; " +
            $"decisions checked {Checks}, different {DecisionDiffs}; boats checked {StateChecks}, different {StateDiffs}";

        /// <summary>Every 5 s while the checker is on: <see cref="Summary"/> (BoatMovingManager.Update, once per frame).</summary>
        internal static void LogSummary(World world)
        {
            if (!Check || Time.Ticks < _logAt)
            {
                return;
            }

            _logAt = Time.Ticks + 5000;
            Log.Info(Summary(world));
        }
    }
}
