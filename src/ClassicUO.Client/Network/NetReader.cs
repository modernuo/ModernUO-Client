// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using ClassicUO.IO;

namespace ClassicUO.Network
{
    /// <summary>
    /// span_reader (net-rework step 2): the packet reader handlers get. With the switch on every read goes to the ModernUO
    /// <see cref="SpanReader"/> (it throws on a short packet; the dispatch catches that and drops the connection); off, to the
    /// old <see cref="StackDataReader"/> (short reads give 0 / ""). The API is SpanReader's, so when the old path is deleted
    /// this type is replaced by SpanReader in the handler signatures. CUO_SPAN_READER_CHECK=1 (developer builds) runs both on
    /// every read and logs the first value or position that differs in a packet (<see cref="SpanBuffers"/>).
    /// </summary>
    public ref struct NetReader
    {
        private SpanReader _new;
        private StackDataReader _old;
        private SpanBufferMode _mode;

        public NetReader(ReadOnlySpan<byte> data) : this(data, SpanBuffers.Mode)
        {
        }

        public NetReader(ReadOnlySpan<byte> data, SpanBufferMode mode)
        {
            _mode = mode;
            _new = mode != SpanBufferMode.Old ? new SpanReader(data) : default;
            _old = mode != SpanBufferMode.New ? new StackDataReader(data) : default;
        }

        public readonly SpanBufferMode Mode => _mode;

        public readonly int Position => _mode == SpanBufferMode.New ? _new.Position : _old.Position;

        public readonly int Length => _mode == SpanBufferMode.New ? _new.Length : (int)_old.Length;

        public readonly int Remaining => _mode == SpanBufferMode.New ? _new.Remaining : _old.Remaining;

        public readonly ReadOnlySpan<byte> Buffer => _mode == SpanBufferMode.New ? _new.Buffer : _old.Buffer;

        public readonly byte this[int index] => Buffer[index];

        public byte ReadByte()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadByte();
            byte o = _old.ReadUInt8();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out byte n, nameof(ReadByte)), n);
        }

        public sbyte ReadSByte()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadSByte();
            sbyte o = _old.ReadInt8();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out sbyte n, nameof(ReadSByte)), n);
        }

        public bool ReadBoolean()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadBoolean();
            bool o = _old.ReadBool();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out bool n, nameof(ReadBoolean)), n);
        }

        public ushort ReadUInt16()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadUInt16();
            ushort o = _old.ReadUInt16BE();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out ushort n, nameof(ReadUInt16)), n);
        }

        public short ReadInt16()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadInt16();
            short o = _old.ReadInt16BE();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out short n, nameof(ReadInt16)), n);
        }

        public uint ReadUInt32()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadUInt32();
            uint o = _old.ReadUInt32BE();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out uint n, nameof(ReadUInt32)), n);
        }

        public int ReadInt32()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadInt32();
            int o = _old.ReadInt32BE();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out int n, nameof(ReadInt32)), n);
        }

        public uint ReadUInt32LE()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadUInt32LE();
            uint o = _old.ReadUInt32LE();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out uint n, nameof(ReadUInt32LE)), n);
        }

        public ulong ReadUInt64()
        {
            if (_mode == SpanBufferMode.New) return _new.ReadUInt64();
            ulong o = _old.ReadUInt64BE();
            if (_mode == SpanBufferMode.Old) return o;
            return Check(o, CheckNew(out ulong n, nameof(ReadUInt64)), n);
        }

        public string ReadCp1252() => ReadString(StringKind.Cp1252, false, -1);

        public string ReadCp1252(int fixedLength) => ReadString(StringKind.Cp1252, false, fixedLength);

        public string ReadCp1252Safe() => ReadString(StringKind.Cp1252, true, -1);

        public string ReadCp1252Safe(int fixedLength) => ReadString(StringKind.Cp1252, true, fixedLength);

        public string ReadBigUni() => ReadString(StringKind.BigUni, false, -1);

        public string ReadBigUni(int fixedLength) => ReadString(StringKind.BigUni, false, fixedLength);

        public string ReadBigUniSafe() => ReadString(StringKind.BigUni, true, -1);

        public string ReadLittleUni() => ReadString(StringKind.LittleUni, false, -1);

        public string ReadLittleUni(int fixedLength) => ReadString(StringKind.LittleUni, false, fixedLength);

        public string ReadUTF8() => ReadString(StringKind.Utf8, false, -1);

        public string ReadUTF8Safe() => ReadString(StringKind.Utf8, true, -1);

        public string ReadUTF8Safe(int fixedLength) => ReadString(StringKind.Utf8, true, fixedLength);

        public int ReadLittleUni(int fixedLength, scoped Span<char> destination)
        {
            if (_mode == SpanBufferMode.New) return _new.ReadLittleUni(fixedLength, destination);
            int o = _old.ReadUnicodeLE(fixedLength, destination);
            if (_mode == SpanBufferMode.Old) return o;
#if CUO_DEVTOOLS
            // the new read decodes into its own buffer; the chars and the count are compared
            Span<char> chars = stackalloc char[destination.Length];
            int at = _new.Position;
            int n = 0;

            try
            {
                n = _new.ReadLittleUni(fixedLength, chars);
            }
            catch (Exception e)
            {
                return Threw(e, at, nameof(ReadLittleUni), o);
            }

            bool same = n == o && chars.Slice(0, n).SequenceEqual(destination.Slice(0, o));

            return Check(o, same, at, nameof(ReadLittleUni), n, same ? null : new string(chars.Slice(0, n)), new string(destination.Slice(0, o)));
#else
            return o;
#endif
        }

        public void Skip(int count)
        {
            if (_mode == SpanBufferMode.New)
            {
                _new.Skip(count);

                return;
            }

            _old.Skip(count);

#if CUO_DEVTOOLS
            if (_mode != SpanBufferMode.Old)
            {
                Move(count, SeekOrigin.Current, nameof(Skip));
            }
#endif
        }

        public int Seek(int offset, SeekOrigin origin)
        {
            if (_mode == SpanBufferMode.New)
            {
                return _new.Seek(offset, origin);
            }

            _old.Seek(origin switch
            {
                SeekOrigin.Current => _old.Position + offset,
                SeekOrigin.End => _old.Length + offset,
                _ => offset
            });

#if CUO_DEVTOOLS
            if (_mode != SpanBufferMode.Old)
            {
                Move(offset, origin, nameof(Seek));
            }
#endif

            return _old.Position;
        }

        private enum StringKind : byte { Cp1252, BigUni, LittleUni, Utf8 }

        private string ReadString(StringKind kind, bool safe, int fixedLength)
        {
            if (_mode == SpanBufferMode.New)
            {
                return ReadNew(ref _new, kind, safe, fixedLength);
            }

            string o = kind switch
            {
                StringKind.Cp1252 => fixedLength < 0 ? _old.ReadASCII(safe) : _old.ReadASCII(fixedLength, safe),
                StringKind.BigUni => fixedLength < 0 ? _old.ReadUnicodeBE(safe) : _old.ReadUnicodeBE(fixedLength, safe),
                StringKind.LittleUni => fixedLength < 0 ? _old.ReadUnicodeLE(safe) : _old.ReadUnicodeLE(fixedLength, safe),
                _ => fixedLength < 0 ? _old.ReadUTF8(safe) : _old.ReadUTF8(fixedLength, safe)
            };

            if (_mode == SpanBufferMode.Old)
            {
                return o;
            }
#if CUO_DEVTOOLS
            int at = _new.Position;
            string n;

            try
            {
                n = ReadNew(ref _new, kind, safe, fixedLength);
            }
            catch (Exception e)
            {
                return Threw(e, at, $"Read{kind}{(safe ? "Safe" : "")}({fixedLength})", o);
            }

            return Check(o, o == n, at, $"Read{kind}{(safe ? "Safe" : "")}({fixedLength})", n, n, o);
#else
            return o;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string ReadNew(ref SpanReader r, StringKind kind, bool safe, int fixedLength) => kind switch
        {
            StringKind.Cp1252 => fixedLength < 0 ? (safe ? r.ReadCp1252Safe() : r.ReadCp1252()) : (safe ? r.ReadCp1252Safe(fixedLength) : r.ReadCp1252(fixedLength)),
            StringKind.BigUni => fixedLength < 0 ? (safe ? r.ReadBigUniSafe() : r.ReadBigUni()) : (safe ? r.ReadBigUniSafe(fixedLength) : r.ReadBigUni(fixedLength)),
            StringKind.LittleUni => fixedLength < 0 ? (safe ? r.ReadLittleUniSafe() : r.ReadLittleUni()) : (safe ? r.ReadLittleUniSafe(fixedLength) : r.ReadLittleUni(fixedLength)),
            _ => fixedLength < 0 ? (safe ? r.ReadUTF8Safe() : r.ReadUTF8()) : (safe ? r.ReadUTF8Safe(fixedLength) : r.ReadUTF8(fixedLength))
        };

        // ---- CUO_SPAN_READER_CHECK: both readers on the same bytes. After the first difference in a packet the reader keeps
        // to the switch's reader only (the other one is no longer aligned), so each packet logs at most one mismatch.

        // Reads the same primitive with the new reader; false when it threw (the old value is then used, or with the switch
        // on the exception is rethrown by Check).
        private bool CheckNew<T>(out T value, string call) where T : unmanaged
        {
            value = default;
#if CUO_DEVTOOLS
            _checkAt = _new.Position;
            _checkCall = call;

            try
            {
                value = call switch
                {
                    nameof(ReadByte) => Cast<byte, T>(_new.ReadByte()),
                    nameof(ReadSByte) => Cast<sbyte, T>(_new.ReadSByte()),
                    nameof(ReadBoolean) => Cast<bool, T>(_new.ReadBoolean()),
                    nameof(ReadUInt16) => Cast<ushort, T>(_new.ReadUInt16()),
                    nameof(ReadInt16) => Cast<short, T>(_new.ReadInt16()),
                    nameof(ReadUInt32) => Cast<uint, T>(_new.ReadUInt32()),
                    nameof(ReadInt32) => Cast<int, T>(_new.ReadInt32()),
                    nameof(ReadUInt32LE) => Cast<uint, T>(_new.ReadUInt32LE()),
                    _ => Cast<ulong, T>(_new.ReadUInt64())
                };
            }
            catch (Exception e)
            {
                _checkThrew = e;

                return false;
            }
#endif
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TTo Cast<TFrom, TTo>(TFrom v) where TFrom : unmanaged where TTo : unmanaged => Unsafe.As<TFrom, TTo>(ref v);

#if CUO_DEVTOOLS
        private int _checkAt;
        private string _checkCall;
        private Exception _checkThrew;
#endif

        private T Check<T>(T o, bool newRead, T n) where T : unmanaged
        {
#if CUO_DEVTOOLS
            if (!newRead)
            {
                var e = _checkThrew;
                _checkThrew = null;

                return Threw(e, _checkAt, _checkCall, o);
            }

            bool same = EqualityComparer<T>.Default.Equals(o, n);

            return Check(o, same, _checkAt, _checkCall, n, same ? null : n.ToString(), same ? null : o.ToString());
#else
            return o;
#endif
        }

#if CUO_DEVTOOLS
        // a compared read: positions must match too
        private T Check<T>(T o, bool same, int at, string call, T n, string newText, string oldText)
        {
            SpanBuffers.Reads++;

            if (same && _new.Position == _old.Position)
            {
                return _mode == SpanBufferMode.CheckNew ? n : o;
            }

            SpanBuffers.ReaderMismatch(Buffer, call, at, oldText ?? o?.ToString(), newText ?? n?.ToString(), _old.Position, _new.Position, null);

            return Diverge() ? n : o;
        }

        // the new reader threw where the old one read: a mismatch; with the switch on the exception is the new path's result
        private T Threw<T>(Exception e, int at, string call, T o)
        {
            SpanBuffers.Reads++;
            SpanBuffers.ReaderMismatch(Buffer, call, at, o?.ToString(), "throws", _old.Position, at, e);

            if (Diverge())
            {
                throw e;
            }

            return o;
        }

        // a seek or skip moved both: the new one may throw past the end where the old one moves anyway
        private void Move(int offset, SeekOrigin origin, string call)
        {
            int at = _new.Position;

            try
            {
                _new.Seek(offset, origin);
            }
            catch (Exception e)
            {
                SpanBuffers.ReaderMismatch(Buffer, call, at, $"to {_old.Position}", "throws", _old.Position, at, e);

                if (Diverge())
                {
                    throw;
                }

                return;
            }

            if (_new.Position != _old.Position)
            {
                SpanBuffers.ReaderMismatch(Buffer, call, at, $"to {_old.Position}", $"to {_new.Position}", _old.Position, _new.Position, null);
                Diverge();
            }
        }

        // keeps the switch's reader only; true when that is the new one
        private bool Diverge()
        {
            bool useNew = _mode == SpanBufferMode.CheckNew;
            _mode = useNew ? SpanBufferMode.New : SpanBufferMode.Old;

            return useNew;
        }
#endif
    }
}
