// Kudos Network Solution (KNS)
// Pooled, zero-allocation-in-steady-state binary writer.
//
// DESIGN LINEAGE (Fusion + Mirror): Mirror's NetworkWriter API shape (simple,
// discoverable Write* methods) with Fusion's obsession for compactness:
// var-ints, quantized floats, smallest-three quaternions.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Kudos.Network.Serialization
{
    public sealed class KudosWriter
    {
        private byte[] _buffer;
        public int Position { get; private set; }

        public KudosWriter(int capacity = 1500) { _buffer = new byte[capacity]; }

        public ArraySegment<byte> ToSegment() => new ArraySegment<byte>(_buffer, 0, Position);
        public void Reset() => Position = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int extra)
        {
            if (Position + extra <= _buffer.Length) return;
            Array.Resize(ref _buffer, Mathf.NextPowerOfTwo(Position + extra));
        }

        public void WriteByte(byte value) { EnsureCapacity(1); _buffer[Position++] = value; }
        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUShort(ushort value)
        {
            EnsureCapacity(2);
            _buffer[Position++] = (byte)value;
            _buffer[Position++] = (byte)(value >> 8);
        }

        public void WriteInt(int value) => WriteUInt((uint)value);

        public void WriteUInt(uint value)
        {
            EnsureCapacity(4);
            _buffer[Position++] = (byte)value;
            _buffer[Position++] = (byte)(value >> 8);
            _buffer[Position++] = (byte)(value >> 16);
            _buffer[Position++] = (byte)(value >> 24);
        }

        public void WriteULong(ulong value) { WriteUInt((uint)value); WriteUInt((uint)(value >> 32)); }

        /// <summary>LEB128 variable-length uint - ids and counts are usually tiny.</summary>
        public void WriteVarUInt(uint value)
        {
            while (value >= 0x80) { WriteByte((byte)(value | 0x80)); value >>= 7; }
            WriteByte((byte)value);
        }

        public void WriteFloat(float value)
        {
            var u = new FloatUnion { f = value };
            WriteUInt(u.u);
        }

        public void WriteString(string value)
        {
            if (string.IsNullOrEmpty(value)) { WriteVarUInt(0); return; }
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteVarUInt((uint)bytes.Length + 1);
            WriteBytes(bytes, 0, bytes.Length);
        }

        public void WriteBytes(byte[] data, int offset, int count)
        {
            EnsureCapacity(count);
            Buffer.BlockCopy(data, offset, _buffer, Position, count);
            Position += count;
        }

        public void WriteSegment(ArraySegment<byte> segment)
        {
            WriteVarUInt((uint)segment.Count);
            WriteBytes(segment.Array, segment.Offset, segment.Count);
        }

        // ------------------------------------------------ Unity types

        public void WriteVector3(Vector3 v) { WriteFloat(v.x); WriteFloat(v.y); WriteFloat(v.z); }

        /// <summary>Millimetre-precision position within +-32km of origin: 12 bytes -> 9.</summary>
        public void WriteVector3Quantized(Vector3 v)
        {
            Quantization.WritePosition(this, v);
        }

        /// <summary>Smallest-three quaternion: 16 bytes -> 4.</summary>
        public void WriteQuaternionQuantized(Quaternion q)
        {
            Quantization.WriteRotation(this, q);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float f;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint u;
        }

        // ------------------------------------------------ pooling (Mirror-style)
        private static readonly Stack<KudosWriter> Pool = new Stack<KudosWriter>();

        public static KudosWriter Rent()
        {
            var w = Pool.Count > 0 ? Pool.Pop() : new KudosWriter();
            w.Reset();
            return w;
        }

        public static void Return(KudosWriter writer) => Pool.Push(writer);
    }
}
