using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Collections;

namespace ClassicUO.UnitTests.Ecs
{
    // The calls 0xF6 and the frame make on a World's boats: the real BoatMovingManager, or the reference below.
    internal interface IBoatCalls
    {
        void AddStep(uint serial, byte speed, Direction movingDir, Direction facingDir, ushort x, ushort y, sbyte z);
        void PushItemToList(uint serial, uint objSerial, int x, int y, int z);
        void ClearEntities(uint serial);
        void ClearSteps(uint serial);
        void Update();
    }

    internal sealed class ManagerBoatCalls : IBoatCalls
    {
        private readonly BoatMovingManager _m;

        public ManagerBoatCalls(World world) => _m = world.BoatMovingManager;

        public void AddStep(uint serial, byte speed, Direction movingDir, Direction facingDir, ushort x, ushort y, sbyte z) =>
            _m.AddStep(serial, speed, movingDir, facingDir, x, y, z);

        public void PushItemToList(uint serial, uint objSerial, int x, int y, int z) => _m.PushItemToList(serial, objSerial, x, y, z);
        public void ClearEntities(uint serial) => _m.ClearEntities(serial);
        public void ClearSteps(uint serial) => _m.ClearSteps(serial);
        public void Update() => _m.Update();
    }

    /// <summary>
    /// T9 deleted <c>ecs_boat_steps</c>' off path: <c>BoatMovingManager</c>'s dictionaries (queued steps and passenger lists
    /// per boat serial) and its frame loop. This is a copy of that code, kept as the reference the step system is compared
    /// with; it writes through the same boat operations as the deleted code did. A multi reload's <c>ClearSteps</c> goes to
    /// the World's real manager, so a test that reloads a multi calls <see cref="ClearSteps"/> on this one itself.
    /// </summary>
    internal sealed class LegacyBoatCalls : IBoatCalls
    {
        private readonly Dictionary<uint, Deque<BoatStep>> _steps = new Dictionary<uint, Deque<BoatStep>>();
        private readonly List<uint> _toRemove = new List<uint>();
        private readonly Dictionary<uint, List<ItemInside>> _items = new Dictionary<uint, List<ItemInside>>();
        private readonly World _world;
        private uint _timePacket;

        public LegacyBoatCalls(World world) => _world = world;

        public void AddStep(uint serial, byte speed, Direction movingDir, Direction facingDir, ushort x, ushort y, sbyte z)
        {
            Item item = _world.GetItem(serial);

            if (item == null || item.IsDestroyed)
            {
                return;
            }

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

            if (empty)
            {
                _world.Writer.SetBoatStepTime(item, Time.Ticks);
            }

            deque.AddToBack(new BoatStep
            {
                Serial = serial,
                TimeDiff = _timePacket == 0 || empty ? BoatStepRules.Velocity(speed) : (int)(Time.Ticks - _timePacket),
                Speed = speed,
                X = x,
                Y = y,
                Z = z,
                MovingDir = movingDir
            });

            _items.Remove(serial);
            _timePacket = Time.Ticks;
        }

        public void ClearSteps(uint serial)
        {
            if (_steps.TryGetValue(serial, out Deque<BoatStep> deque) && deque.Count != 0)
            {
                Item multiItem = _world.FindItem(serial);

                if (multiItem != null)
                {
                    _world.Writer.ResetBoatOffset(multiItem);
                }

                if (_items.TryGetValue(serial, out var list))
                {
                    foreach (ItemInside it in list)
                    {
                        Entity ent = _world.Get(it.Serial);

                        if (ent != null)
                        {
                            _world.Writer.ResetBoatOffset(ent);
                        }
                    }

                    list.Clear();
                }

                deque.Clear();
            }
        }

        public void ClearEntities(uint serial) => _items.Remove(serial);

        public void PushItemToList(uint serial, uint objSerial, int x, int y, int z)
        {
            if (!_items.TryGetValue(serial, out var list))
            {
                list = new List<ItemInside>();
                _items[serial] = list;
            }

            Span<ItemInside> span = CollectionsMarshal.AsSpan(list);

            for (int i = 0; i < span.Length; i++)
            {
                ref ItemInside item = ref span[i];

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

            list.Add(new ItemInside { Serial = objSerial, X = x, Y = y, Z = z });
        }

        public void Update()
        {
            foreach (Deque<BoatStep> deques in _steps.Values)
            {
                while (deques.Count != 0)
                {
                    ref BoatStep step = ref deques.Front();
                    Item item = _world.GetItem(step.Serial);

                    if (item == null || item.IsDestroyed)
                    {
                        _toRemove.Add(step.Serial);

                        break;
                    }

                    BoatStepEntry entry = step.Entry;
                    BoatStepRules.Decide(in entry, item.X, item.Y, item.Z, item.LastStepTime, Time.Ticks, out BoatStepDecision d);

                    if (d.Interpolate)
                    {
                        _world.Writer.InterpolateBoat(item, d.X, d.Y, d.Z);
                    }

                    _world.HouseManager.TryGetHouse(item, out House house);

                    if (d.Remove)
                    {
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

            for (int i = 0; i < _toRemove.Count; i++)
            {
                _steps.Remove(_toRemove[i]);
                _items.Remove(_toRemove[i]);
            }

            _toRemove.Clear();
        }

        private void UpdateEntitiesInside(uint serial, bool removeStep, int x, int y, int z)
        {
            if (!_items.TryGetValue(serial, out var list))
            {
                return;
            }

            Item item = _world.FindItem(serial);

            foreach (ItemInside it in list)
            {
                Entity entity = _world.Get(it.Serial);

                if (entity == null || entity.IsDestroyed)
                {
                    continue;
                }

                if (removeStep)
                {
                    _world.Writer.MoveBoatPassenger(entity, (ushort)(x - it.X), (ushort)(y - it.Y), (sbyte)(z - it.Z));
                }
                else if (item != null)
                {
                    _world.Writer.FollowBoat(entity, item.Offset);
                }
            }
        }

        private struct BoatStep
        {
            public uint Serial;
            public int TimeDiff;
            public ushort X, Y;
            public sbyte Z;
            public byte Speed;
            public Direction MovingDir;

            public BoatStepEntry Entry => new BoatStepEntry
                { TimeDiff = TimeDiff, X = X, Y = Y, Z = Z, Speed = Speed, Direction = (byte)MovingDir };
        }

        private struct ItemInside
        {
            public uint Serial;
            public int X, Y, Z;
        }
    }
}
