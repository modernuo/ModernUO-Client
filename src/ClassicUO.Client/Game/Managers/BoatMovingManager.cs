// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Network;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Entity = ClassicUO.Game.GameObjects.Entity;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// Smooth boat movement (0xF6 with the profile's <c>UseSmoothBoatMovement</c>). The queued steps and the passenger lists
    /// are the boat's <c>BoatSteps</c> component, filled by WorldWriter's boat operations and advanced by
    /// <see cref="BoatStepSystem"/> (T9 deleted <c>ecs_boat_steps</c>' off path, this manager's dictionaries).
    /// </summary>
    internal sealed class BoatMovingManager
    {
        private uint _timePacket;
        private readonly World _world;
        private readonly BoatStepSystem _system;

        public BoatMovingManager(World world)
        {
            _world = world;
            _system = new BoatStepSystem(world);
        }

        /// <summary>The step system (its boat order is kept by <c>WorldWriter.QueueBoatStep</c>).</summary>
        internal BoatStepSystem Steps => _system;

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
            Item item = _world.GetItem(serial);

            if (item == null || item.IsDestroyed)
            {
                return;
            }

            _world.Writer.QueueBoatStep(item, speed, movingDir, x, y, z, _timePacket);
            _timePacket = Time.Ticks;
        }

        public void ClearSteps(uint serial)
        {
            Item boat = _world.FindItem(serial);

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
            else if (_system.TakeParked(serial, out BoatSteps parked))
            {
                // the boat's record was removed with steps queued and the new one has none yet (a multi reloaded as it is
                // recreated): the old dictionaries still held the queue and passengers under the serial
                if (boat != null)
                {
                    _world.Writer.ResetBoatOffset(boat);
                }

                for (int i = 0; i < parked.PassengerCount; i++)
                {
                    Entity ent = _world.Get(parked.Passengers[i].Serial);

                    if (ent != null)
                    {
                        _world.Writer.ResetBoatOffset(ent);
                    }
                }
            }
        }

#if CUO_DEVTOOLS
        /// <summary>
        /// Dev trace (T1 part 7): null when <paramref name="serial"/> has no queued step (<see cref="ClearSteps"/> then
        /// resets nothing), else the queue length, the boat's offset and its passengers' offsets that are not zero.
        /// </summary>
        internal string DescribeQueuedSteps(uint serial)
        {
            Item boat = _world.FindItem(serial);
            int passengers = 0, moved = 0;
            BoatSteps s;

            if (boat != null && BoatStepSystem.TryBoatSteps(boat, out EcsEntity e) && e.GetComponent<BoatSteps>().Count != 0)
            {
                s = e.GetComponent<BoatSteps>();
            }
            else if (!_system.TryParked(serial, out s))
            {
                return null;
            }

            for (int i = 0; i < s.PassengerCount; i++)
            {
                passengers++;

                if (_world.Get(s.Passengers[i].Serial) is { } ent && ent.ScreenOffset != Microsoft.Xna.Framework.Vector3.Zero)
                {
                    moved++;
                }
            }

            return $"steps {s.Count}, boat offset {(boat == null ? "none" : ItemOffsets.Read(boat).ToString())}, " +
                $"passengers {passengers} ({moved} with a non-zero offset)";
        }
#endif

        public void ClearEntities(uint serial)
        {
            if (_world.FindItem(serial) is { } boat)
            {
                _world.Writer.DropBoatSteps(boat, steps: false);
            }
            else
            {
                _system.DropParkedPassengers(serial);
            }
        }


        public void PushItemToList(uint serial, uint objSerial, int x, int y, int z)
        {
            if (_world.FindItem(serial) is { } boat)
            {
                _world.Writer.SetBoatPassenger(boat, objSerial, x, y, z);
            }
        }

        public void Update()
        {
            _system.Run();
        }
    }
}
