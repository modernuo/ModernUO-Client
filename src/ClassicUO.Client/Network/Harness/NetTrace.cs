// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System;
using System.IO;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Network.Harness
{
    /// <summary>
    /// The canonical trace of a receive run (net-rework parity harness). One line per dispatched packet:
    /// <c>P seq id len bytesHash oN effectHash</c>, where the effect hash covers the WorldWriter operations the handler ran
    /// (name, serial, and the subject's state when the operation closed) and N is their count; one line per checkpoint:
    /// <c>C seq items mobiles worldHash</c> (an order-independent digest of every item and mobile); and <c>E seq</c> at the
    /// end. Two runs of one capture through equivalent receive paths give identical traces; tools/net-replay/nettrace_diff.py
    /// prints the first divergence. With CUO_NET_TRACE_OPS=1 each operation is also listed under its packet.
    /// </summary>
    internal sealed class NetTrace : IDisposable
    {
        // set while any trace exists, so the WorldWriter hook costs one static read when no trace runs
        internal static bool Any;

        // the trace whose packet is being dispatched on this thread (null between packets and when no trace runs)
        [ThreadStatic] private static NetTrace t_dispatching;

        internal static NetTrace Dispatching => Any ? t_dispatching : null;

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private readonly TextWriter _out;
        private readonly bool _ops;
        private readonly int _checkpointEvery;
        private long _seq;
        private ulong _effect;
        private int _opCount;
        private byte _id;
        private int _len;
        private ulong _bytes;

        public NetTrace(TextWriter output, string header, bool ops = false, int checkpointEvery = 500)
        {
            _out = output;
            _ops = ops;
            _checkpointEvery = checkpointEvery;
            _out.WriteLine($"# nettrace v1 {header}");
            Any = true;
        }

        public long Packets => _seq;

        public static ulong Hash(ReadOnlySpan<byte> data, ulong h = FnvOffset)
        {
            foreach (byte b in data)
            {
                h = (h ^ b) * FnvPrime;
            }

            return h;
        }

        private static ulong Mix(ulong h, ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                h = (h ^ (byte)v) * FnvPrime;
                v >>= 8;
            }

            return h;
        }

        private static ulong Mix(ulong h, string s)
        {
            if (s == null)
            {
                return Mix(h, 0xFFFF_FFFFUL);
            }

            foreach (char c in s)
            {
                h = Mix(h, c);
            }

            return Mix(h, (ulong)s.Length);
        }

        public void BeginPacket(ReadOnlySpan<byte> packet)
        {
            _id = packet.IsEmpty ? (byte)0 : packet[0];
            _len = packet.Length;
            _bytes = Hash(packet);
            _effect = FnvOffset;
            _opCount = 0;
            t_dispatching = this;

            if (_ops)
            {
                _out.WriteLine($"P {_seq} {_id:X2} {_len} {_bytes:X16}");
            }
        }

        public void EndPacket(World world)
        {
            t_dispatching = null;
            _out.WriteLine(_ops ? $"= {_seq} o{_opCount} {_effect:X16}" : $"P {_seq} {_id:X2} {_len} {_bytes:X16} o{_opCount} {_effect:X16}");
            _seq++;

            if (world != null && _checkpointEvery > 0 && _seq % _checkpointEvery == 0)
            {
                Checkpoint(world);
            }
        }

        /// <summary>A WorldWriter operation was admitted during the packet.</summary>
        public void Op(string operation, uint serial)
        {
            _opCount++;
            _effect = Mix(Mix(_effect, operation), serial);

            if (_ops)
            {
                _out.WriteLine($"  o {operation} {serial:X8}");
            }
        }

        /// <summary>The operation's scope closed: its subject's state is part of the effect.</summary>
        public void OpEnd(Entity subject)
        {
            ulong h = EntityHash(subject);
            _effect = Mix(_effect, h);

            if (_ops)
            {
                _out.WriteLine($"  s {h:X16}");
            }
        }

        public static ulong EntityHash(Entity e)
        {
            if (e == null)
            {
                return 0;
            }

            ulong h = FnvOffset;
            h = Mix(h, e.Serial);
            h = Mix(h, e.Graphic);
            h = Mix(h, e.Hue);
            h = Mix(h, ((ulong)e.X << 32) | ((ulong)e.Y << 16) | (byte)e.Z);
            h = Mix(h, (ulong)e.Flags);
            h = Mix(h, (ulong)e.Direction);
            h = Mix(h, ((ulong)e.Hits << 16) | e.HitsMax);
            h = Mix(h, e.Name);

            if (e is Item it)
            {
                h = Mix(h, it.Amount);
                h = Mix(h, it.Container);
                h = Mix(h, (ulong)it.Layer);
            }
            else if (e is Mobile m)
            {
                h = Mix(h, (ulong)m.NotorietyFlag);
                h = Mix(h, m.IsDead ? 1UL : 0UL);
            }

            return h;
        }

        public void Checkpoint(World world)
        {
            ulong sum = 0;
            int items = 0, mobiles = 0;

            foreach (Item it in world.Items.Values)
            {
                sum += EntityHash(it);
                items++;
            }

            foreach (Mobile m in world.Mobiles.Values)
            {
                sum += EntityHash(m);
                mobiles++;
            }

            _out.WriteLine($"C {_seq} {items} {mobiles} {sum:X16}");
        }

        public void End(World world)
        {
            if (world != null)
            {
                Checkpoint(world);
            }

            _out.WriteLine($"E {_seq}");
            _out.Flush();
        }

        public void Flush() => _out.Flush();

        public void Dispose()
        {
            if (ReferenceEquals(t_dispatching, this))
            {
                t_dispatching = null;
            }

            _out.Dispose();
        }
    }
}
#endif
