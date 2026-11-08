// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Network;
using ClassicUO.Utility.Collections;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Entity = ClassicUO.Game.GameObjects.Entity;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// Smooth boat movement (0xF6 with the profile's <c>UseSmoothBoatMovement</c>). With <c>ecs_boat_steps</c> off the queued
    /// steps and the passenger lists live in this manager's dictionaries; with it on they are the boat's <c>BoatSteps</c>
    /// component, filled by WorldWriter's boat operations and advanced by <see cref="BoatStepSystem"/>, and the dictionaries
    /// are only the CUO_BOAT_CHECK shadow (kept by the same calls, writing nothing).
    /// </summary>
    internal sealed class BoatMovingManager
    {
        private readonly Dictionary<uint, Deque<BoatStep>> _steps = new Dictionary<uint, Deque<BoatStep>>();
        private readonly List<uint> _toRemove = new List<uint>();
        private readonly Dictionary<uint, List<ItemInside>> _items = new Dictionary<uint, List<ItemInside>>();

        private uint _timePacket;
        private readonly World _world;
        private readonly BoatStepSystem _system;

        public BoatMovingManager(World world)
        {
            _world = world;
            _system = new BoatStepSystem(world);
        }

        /// <summary><c>ecs_boat_steps</c>: the step system (its boat order is kept by <c>WorldWriter.QueueBoatStep</c>).</summary>
        internal BoatStepSystem Steps => _system;

        private bool On => _world.BoatStepsOn;

        // the dictionaries are kept: the old path, or the checker's shadow of the new one
        private bool Legacy => !_world.BoatStepsOn || BoatStepSystem.Check;

        public void MoveRequest(Direction direciton, byte speed)
        {
            NetClient.Socket.Send_MultiBoatMoveRequest(_world.Player, direciton, speed);
            _timePacket = Time.Ticks;
        }


        public void AddStep
        (
            uint serial,
            byte speed,
            Direction movingDir,
            Direction facingDir,
            ushort x,
            ushort y,
            sbyte z
        )
        {
            Item item = _world.Items.Get(serial);

            if (item == null || item.IsDestroyed)
            {
                return;
            }

            if (Legacy)
            {
                LegacyAddStep(item, speed, movingDir, x, y, z, write: !On);
            }

            if (On)
            {
                _world.Writer.QueueBoatStep(item, speed, movingDir, x, y, z, _timePacket);
            }

            _timePacket = Time.Ticks;
            Console.WriteLine("CURRENT PACKET TIME: {0}", _timePacket);
        }

        private void LegacyAddStep(Item item, byte speed, Direction movingDir, ushort x, ushort y, sbyte z, bool write)
        {
            uint serial = item.Serial;

            if (!_steps.TryGetValue(serial, out Deque<BoatStep> deque))
            {
                deque = new Deque<BoatStep>();
                _steps[serial] = deque;
            }

            bool empty = deque.Count == 0;

            while (deque.Count > 5)
            {
                deque.RemoveFromFront();
            }

            if (empty && write)
            {
                // T1 (funnel part 6)
                _world.Writer.SetBoatStepTime(item, Time.Ticks);
            }

            BoatStep step = new BoatStep();
            step.Serial = serial;
            step.TimeDiff = _timePacket == 0 || empty ? BoatStepRules.Velocity(speed) : (int) (Time.Ticks - _timePacket);

            step.Speed = speed;
            step.X = x;
            step.Y = y;
            step.Z = z;
            step.MovingDir = movingDir;
            deque.AddToBack(step);

            _items.Remove(serial);
        }

        public void ClearSteps(uint serial)
        {
            if (!On)
            {
                LegacyClearSteps(serial, write: true);

                return;
            }

            Item boat = _world.Items.Get(serial);

            if (boat != null && BoatStepSystem.TryBoatSteps(boat, out EcsEntity e))
            {
                ref BoatSteps s = ref e.GetComponent<BoatSteps>();

                if (s.Count != 0)
                {
                    // copies: the resets below can move the record to another archetype
                    BoatPassenger[] passengers = s.Passengers;
                    int count = s.PassengerCount;

                    // T1 (funnel part 6)
                    _world.Writer.ResetBoatOffset(boat);

                    for (int i = 0; i < count; i++)
                    {
                        Entity ent = _world.Get(passengers[i].Serial);

                        if (ent != null)
                        {
                            _world.Writer.ResetBoatOffset(ent);
                        }
                    }

                    _world.Writer.DropBoatSteps(boat, steps: true);
                }
            }

            if (BoatStepSystem.Check)
            {
                LegacyClearSteps(serial, write: false);
            }
        }

        private void LegacyClearSteps(uint serial, bool write)
        {
            if (_steps.TryGetValue(serial, out Deque<BoatStep> deque) && deque.Count != 0)
            {
                Item multiItem = _world.Items.Get(serial);

                if (multiItem != null && write)
                {
                    // T1 (funnel part 6)
                    _world.Writer.ResetBoatOffset(multiItem);
                }

                if (_items.TryGetValue(serial, out var list))
                {
                    var span = CollectionsMarshal.AsSpan(list);
                    for (int i = 0; write && i < span.Length; i++)
                    {
                        ref var it = ref span[i];

                        Entity ent = _world.Get(it.Serial);

                        if (ent == null)
                        {
                            continue;
                        }

                        _world.Writer.ResetBoatOffset(ent);
                    }

                    list.Clear();
                }

                deque.Clear();
            }
        }

#if CUO_DEVTOOLS
        /// <summary>
        /// Dev trace (T1 part 7): null when <paramref name="serial"/> has no queued step (<see cref="ClearSteps"/> then
        /// resets nothing), else the queue length, the boat's offset and its passengers' offsets that are not zero.
        /// </summary>
        internal string DescribeQueuedSteps(uint serial)
        {
            Item boat = _world.Items.Get(serial);
            int steps, passengers = 0, moved = 0;

            if (On)
            {
                if (boat == null || !BoatStepSystem.TryBoatSteps(boat, out EcsEntity e) || e.GetComponent<BoatSteps>().Count == 0)
                {
                    return null;
                }

                BoatSteps s = e.GetComponent<BoatSteps>();
                steps = s.Count;

                for (int i = 0; i < s.PassengerCount; i++)
                {
                    passengers++;

                    if (_world.Get(s.Passengers[i].Serial) is { } ent && (ent.Offset.X != 0 || ent.Offset.Y != 0 || ent.Offset.Z != 0))
                    {
                        moved++;
                    }
                }
            }
            else
            {
                if (!_steps.TryGetValue(serial, out Deque<BoatStep> deque) || deque.Count == 0)
                {
                    return null;
                }

                steps = deque.Count;

                if (_items.TryGetValue(serial, out var list))
                {
                    foreach (ItemInside it in list)
                    {
                        passengers++;

                        if (_world.Get(it.Serial) is { } ent && (ent.Offset.X != 0 || ent.Offset.Y != 0 || ent.Offset.Z != 0))
                        {
                            moved++;
                        }
                    }
                }
            }

            return $"steps {steps}, boat offset {(boat == null ? "none" : $"({boat.Offset.X},{boat.Offset.Y},{boat.Offset.Z})")}, " +
                $"passengers {passengers} ({moved} with a non-zero offset)";
        }
#endif

        public void ClearEntities(uint serial)
        {
            if (Legacy)
            {
                _items.Remove(serial);
            }

            if (On && _world.Items.Get(serial) is { } boat)
            {
                _world.Writer.DropBoatSteps(boat, steps: false);
            }
        }


        public void PushItemToList(uint serial, uint objSerial, int x, int y, int z)
        {
            if (On && _world.Items.Get(serial) is { } boat)
            {
                _world.Writer.SetBoatPassenger(boat, objSerial, x, y, z);
            }

            if (!Legacy)
            {
                return;
            }

            if (!_items.TryGetValue(serial, out var list))
            {
                list = new List<ItemInside>();

                _items[serial] = list;
            }

            var span = CollectionsMarshal.AsSpan(list);
            for (int i = 0; i < span.Length; i++)
            {
                ref var item = ref span[i];

                if (!SerialHelper.IsValid(item.Serial))
                {
                    break;
                }

                if (item.Serial == objSerial)
                {
                    item.X = x;
                    item.Y = y;
                    item.Z = z;

                    return;
                }
            }

            list.Add
            (
                new ItemInside
                {
                    Serial = objSerial,
                    X = x,
                    Y = y,
                    Z = z
                }
            );
        }

        public void Update()
        {
            ItemOffsets.LogSummary(_world);
            BoatStepSystem.LogSummary(_world);

            if (On)
            {
                _system.Run(BoatStepSystem.Check ? this : null);

                return;
            }

            foreach (Deque<BoatStep> deques in _steps.Values)
            {
                while (deques.Count != 0)
                {
                    ref BoatStep step = ref deques.Front();

                    Item item = _world.Items.Get(step.Serial);

                    if (item == null || item.IsDestroyed)
                    {
                        _toRemove.Add(step.Serial);

                        break;
                    }

                    BoatStepEntry entry = step.Entry;
                    BoatStepRules.Decide(in entry, item.X, item.Y, item.Z, item.LastStepTime, Time.Ticks, out BoatStepDecision d);

                    if (d.Interpolate)
                    {
                        // T1 (funnel part 7, group 4)
                        _world.Writer.InterpolateBoat(item, d.X, d.Y, d.Z);
                    }

                    _world.HouseManager.TryGetHouse(item, out House house);

                    if (d.Remove)
                    {
                        // T1 (funnel part 6): the step's position, screen position, offset and tile (the deque is the
                        // manager's own state: the step is copied before it leaves)
                        ushort stepX = step.X, stepY = step.Y;
                        sbyte stepZ = step.Z;
                        _world.Writer.CompleteBoatStep(item, stepX, stepY, stepZ);
                        deques.RemoveFromFront();

                        house?.Generate(true, true, true);

                        UpdateEntitiesInside(item, true, stepX, stepY, stepZ);

                        _world.Writer.SetBoatStepTime(item, Time.Ticks);
                    }
                    else
                    {
                        if (house != null)
                        {
                            // T1 (funnel part 7, group 4)
                            _world.Writer.FollowBoatComponents(item, house);
                        }

                        UpdateEntitiesInside(item, false, item.X, item.Y, item.Z);
                    }

                    if (!d.DirectionChange)
                    {
                        break;
                    }
                }
            }


            if (_toRemove.Count != 0)
            {
                for (int i = 0; i < _toRemove.Count; i++)
                {
                    _steps.Remove(_toRemove[i]);
                    _items.Remove(_toRemove[i]);
                }

                _toRemove.Clear();
            }
        }

        private void UpdateEntitiesInside(uint serial, bool removeStep, int x, int y, int z)
        {
            if (_items.TryGetValue(serial, out var list))
            {
                Item item = _world.Items.Get(serial);

                var span = CollectionsMarshal.AsSpan(list);
                for (int i = 0; i < span.Length; i++)
                {
                    ref var it = ref span[i];

                    Entity entity = _world.Get(it.Serial);

                    if (entity == null || entity.IsDestroyed)
                    {
                        continue;
                    }

                    if (removeStep)
                    {
                        // T1 (funnel part 6)
                        _world.Writer.MoveBoatPassenger(entity, (ushort) (x - it.X), (ushort) (y - it.Y), (sbyte) (z - it.Z));
                    }
                    else
                    {
                        if (item != null)
                        {
                            // T1 (funnel part 7, group 4)
                            _world.Writer.FollowBoat(entity, item.Offset);
                        }
                    }
                }
            }
        }

        // ---- CUO_BOAT_CHECK: the dictionaries as BoatStepSystem's shadow (ecs_boat_steps on). They follow the same calls
        // (AddStep, PushItemToList, ClearEntities, ClearSteps) without writing, drop a step when the system completes one,
        // and are compared with the component at every decision and after every frame.

        /// <summary>The shadow's front step and its decision from the Item's fields, against the system's.</summary>
        internal void ShadowDecide(Item item, in BoatStepEntry step, in BoatStepDecision d)
        {
            BoatStepSystem.Checks++;

            if (!_steps.TryGetValue(item.Serial, out Deque<BoatStep> deque) || deque.Count == 0)
            {
                BoatStepSystem.Diff(true, $"0x{item.Serial:X8}: the component has a step to {Text(step)}, the shadow none");

                return;
            }

            BoatStepEntry front = deque.Front().Entry;

            if (!SameStep(in front, in step))
            {
                BoatStepSystem.Diff(true, $"0x{item.Serial:X8}: front step ecs {Text(step)} | shadow {Text(front)}");
            }

            BoatStepRules.Decide(in front, item.X, item.Y, item.Z, item.LastStepTime, Time.Ticks, out BoatStepDecision legacy);

            if (!legacy.Equals(in d))
            {
                BoatStepSystem.Diff(true, $"0x{item.Serial:X8} at {item.X},{item.Y},{item.Z}: decision ecs {d} | shadow {legacy}");
            }
        }

        internal void ShadowPop(uint serial)
        {
            if (_steps.TryGetValue(serial, out Deque<BoatStep> deque) && deque.Count != 0)
            {
                deque.RemoveFromFront();
            }
        }

        /// <summary>After the frame: every boat the system stepped or the shadow holds is compared (queue, step clock,
        /// passengers), and the shadow's boats whose Item is gone are dropped (the old Update's removal).</summary>
        internal void ShadowFrameEnd(List<uint> stepped)
        {
            for (int i = 0; i < stepped.Count; i++)
            {
                CompareBoat(stepped[i]);
            }

            foreach (uint serial in _steps.Keys)
            {
                Item item = _world.Items.Get(serial);

                if (item == null || item.IsDestroyed)
                {
                    _toRemove.Add(serial);
                }
                else if (!stepped.Contains(serial))
                {
                    CompareBoat(serial);
                }
            }

            for (int i = 0; i < _toRemove.Count; i++)
            {
                _steps.Remove(_toRemove[i]);
                _items.Remove(_toRemove[i]);
            }

            _toRemove.Clear();
        }

        private void CompareBoat(uint serial)
        {
            Item item = _world.Items.Get(serial);

            if (item == null || item.IsDestroyed)
            {
                return;
            }

            BoatStepSystem.StateChecks++;
            string diff = ShadowDiff(item);

            if (diff != null)
            {
                BoatStepSystem.Diff(false, $"0x{serial:X8}: {diff}");
            }
        }

        /// <summary>Null when the boat's component (none = empty) equals the shadow's queue, step clock and passenger list,
        /// else the first difference.</summary>
        internal string ShadowDiff(Item item)
        {
            BoatSteps s = BoatStepSystem.TryBoatSteps(item, out EcsEntity e) ? e.GetComponent<BoatSteps>() : default;
            _steps.TryGetValue(item.Serial, out Deque<BoatStep> deque);
            int count = deque?.Count ?? 0;

            if (s.Count != count)
            {
                return $"steps ecs {s.Count} | shadow {count}";
            }

            for (int k = 0; k < count; k++)
            {
                BoatStepEntry a = BoatStepRules.At(ref s, k), b = deque[k].Entry;

                if (!SameStep(in a, in b))
                {
                    return $"step {k} ecs {Text(a)} | shadow {Text(b)}";
                }
            }

            if (count != 0 && s.LastStepTime != item.LastStepTime)
            {
                return $"step clock ecs {s.LastStepTime} | item {item.LastStepTime}";
            }

            _items.TryGetValue(item.Serial, out List<ItemInside> list);
            int passengers = list?.Count ?? 0;

            if (s.PassengerCount != passengers)
            {
                return $"passengers ecs {s.PassengerCount} | shadow {passengers}";
            }

            for (int k = 0; k < passengers; k++)
            {
                BoatPassenger a = s.Passengers[k];
                ItemInside b = list[k];

                if (a.Serial != b.Serial || a.X != b.X || a.Y != b.Y || a.Z != b.Z)
                {
                    return $"passenger {k} ecs 0x{a.Serial:X8} ({a.X},{a.Y},{a.Z}) | shadow 0x{b.Serial:X8} ({b.X},{b.Y},{b.Z})";
                }
            }

            return null;
        }

        private static bool SameStep(in BoatStepEntry a, in BoatStepEntry b) =>
            a.TimeDiff == b.TimeDiff && a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.Speed == b.Speed && a.Direction == b.Direction;

        private static string Text(in BoatStepEntry s) => $"{s.X},{s.Y},{s.Z} dir {s.Direction} speed {s.Speed} time {s.TimeDiff}";

        private struct BoatStep
        {
            public uint Serial;
            public int TimeDiff;
            public ushort X, Y;
            public sbyte Z;
            public byte Speed;
            public Direction MovingDir;

            public BoatStepEntry Entry => new BoatStepEntry { TimeDiff = TimeDiff, X = X, Y = Y, Z = Z, Speed = Speed, Direction = (byte)MovingDir };
        }

        private struct ItemInside
        {
            public uint Serial;
            public int X, Y, Z;
        }
    }
}
