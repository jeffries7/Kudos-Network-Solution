// Kudos Network Solution (KNS)
// Binary reader - exact mirror of KudosWriter.

using System;
using System.Text;
using UnityEngine;

namespace Kudos.Network.Serialization
{
    public sealed class KudosReader
    {
        private byte[] _buffer;
        private int _end;
        public int Position { get; private set; }
        public int Remaining => _end - Position;

        /// <summary>Advance past `count` bytes without reading (length-prefixed skip).</summary>
        public void Skip(int count)
        {
            if (Position + count > _end) throw new IndexOutOfRangeException("[KNS] Skip past end of buffer");
            Position += count;
        }

        public KudosReader(ArraySegment<byte> segment) => SetBuffer(segment);

        public void SetBuffer(ArraySegment<byte> segment)
        {
            _buffer = segment.Array;
            Position = segment.Offset;
            _end = segment.Offset + segment.Count;
        }

        public byte ReadByte()
        {
            if (Position >= _end) throw new IndexOutOfRangeException("[KNS] Read past end of buffer");
            return _buffer[Position++];
        }

        public bool ReadBool() => ReadByte() != 0;

        public ushort ReadUShort() => (ushort)(ReadByte() | ReadByte() << 8);

        public uint ReadUInt()
            => (uint)(ReadByte() | ReadByte() << 8 | ReadByte() << 16 | ReadByte() << 24);

        public int ReadInt() => (int)ReadUInt();

        public ulong ReadULong() => ReadUInt() | ((ulong)ReadUInt() << 32);

        public uint ReadVarUInt()
        {
            uint value = 0; int shift = 0;
            while (true)
            {
                byte b = ReadByte();
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
                if (shift > 35) throw new FormatException("[KNS] Malformed var-int");
            }
        }

        public float ReadFloat()
        {
            var u = new FloatUnion { u = ReadUInt() };
            return u.f;
        }

        public string ReadString()
        {
            uint length = ReadVarUInt();
            if (length == 0) return "";
            int count = (int)length - 1;
            var s = Encoding.UTF8.GetString(_buffer, Position, count);
            Position += count;
            return s;
        }

        public ArraySegment<byte> ReadSegment()
        {
            int count = (int)ReadVarUInt();
            var seg = new ArraySegment<byte>(_buffer, Position, count);
            Position += count;
            return seg;
        }

        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        public Vector3 ReadVector3Quantized() => Quantization.ReadPosition(this);
        public Quaternion ReadQuaternionQuantized() => Quantization.ReadRotation(this);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float f;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint u;
        }
    }
}
