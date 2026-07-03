// Kudos Network Solution (KNS)
// Wire quantization.
//
// DESIGN LINEAGE (Fusion / Coherence): both compress aggressively because
// bandwidth is the P2P host's scarcest resource. For a 32-player VR room the
// host re-broadcasts everyone's head + two hands; naive floats would be
// 3*(12+16)=84 bytes per avatar per tick. Quantized: 3*(9+4)=39 bytes,
// before delta compression.
//
//   Position: 3 x 24-bit fixed point, 1mm resolution, +-8388m range (9 bytes)
//   Rotation: smallest-three, 10 bits per component + 2-bit index (4 bytes)
//
// 1mm / ~0.1 degree is invisible in a slow-paced social VR context.

using UnityEngine;

namespace Kudos.Network.Serialization
{
    public static class Quantization
    {
        private const float PositionResolution = 0.001f;                 // 1 mm
        private const float PositionRange = (1 << 23) * PositionResolution;

        public static void WritePosition(KudosWriter w, Vector3 v)
        {
            WriteFixed24(w, v.x);
            WriteFixed24(w, v.y);
            WriteFixed24(w, v.z);
        }

        public static Vector3 ReadPosition(KudosReader r)
            => new Vector3(ReadFixed24(r), ReadFixed24(r), ReadFixed24(r));

        private static void WriteFixed24(KudosWriter w, float value)
        {
            value = Mathf.Clamp(value, -PositionRange, PositionRange - PositionResolution);
            int q = Mathf.RoundToInt(value / PositionResolution) + (1 << 23);
            w.WriteByte((byte)q);
            w.WriteByte((byte)(q >> 8));
            w.WriteByte((byte)(q >> 16));
        }

        private static float ReadFixed24(KudosReader r)
        {
            int q = r.ReadByte() | r.ReadByte() << 8 | r.ReadByte() << 16;
            return (q - (1 << 23)) * PositionResolution;
        }

        // ------------------------------------------------ smallest-three rotation

        private const float InvSqrt2 = 0.70710678f;

        public static void WriteRotation(KudosWriter w, Quaternion q)
        {
            // Find largest-magnitude component; encode the other three at 10 bits each.
            float ax = Mathf.Abs(q.x), ay = Mathf.Abs(q.y), az = Mathf.Abs(q.z), aw = Mathf.Abs(q.w);
            int largest = 0; float largestVal = ax;
            if (ay > largestVal) { largest = 1; largestVal = ay; }
            if (az > largestVal) { largest = 2; largestVal = az; }
            if (aw > largestVal) { largest = 3; }

            // Ensure the omitted (largest) component is positive so it can be reconstructed.
            float sign = Get(q, largest) < 0 ? -1f : 1f;
            uint packed = (uint)largest;
            int shift = 2;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                float c = Get(q, i) * sign;                              // in [-1/sqrt2, 1/sqrt2]
                uint qc = (uint)Mathf.RoundToInt((c / InvSqrt2 * 0.5f + 0.5f) * 1023f);
                packed |= (qc & 0x3FF) << shift;
                shift += 10;
            }
            w.WriteUInt(packed);
        }

        public static Quaternion ReadRotation(KudosReader r)
        {
            uint packed = r.ReadUInt();
            int largest = (int)(packed & 0x3);
            var values = new float[4];
            int shift = 2;
            float sumSq = 0f;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                uint qc = (packed >> shift) & 0x3FF;
                float c = ((qc / 1023f) * 2f - 1f) * InvSqrt2;
                values[i] = c;
                sumSq += c * c;
                shift += 10;
            }
            values[largest] = Mathf.Sqrt(Mathf.Max(0f, 1f - sumSq));
            return new Quaternion(values[0], values[1], values[2], values[3]).normalized;
        }

        private static float Get(Quaternion q, int i)
            => i == 0 ? q.x : i == 1 ? q.y : i == 2 ? q.z : q.w;
    }
}
