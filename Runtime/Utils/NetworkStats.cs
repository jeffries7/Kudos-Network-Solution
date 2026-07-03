// Kudos Network Solution (KNS)
// NetworkStats - per-channel bandwidth accounting.
//
// DESIGN LINEAGE (Photon Fusion's Network Statistics window): visibility is a
// feature. During integration the first question is always "why is the host
// uploading 2 Mbps?" - and the answer is only findable if the transport counts
// every byte it moves, per channel, per direction.
//
// WHY a static ring of 1-second windows instead of live counters: raw
// counters only tell you totals; rates are what you tune against. We close a
// window every second (driven lazily by whoever reads or writes - no Update
// loop needed) and expose the last completed window as the "current rate".
//
// Thread-safety: WebRTC callbacks can arrive off the main thread, so counters
// use Interlocked. Snapshot reads are torn-tolerant (stats, not accounting).

using System;
using System.Threading;
using Kudos.Network.Transport;
using UnityEngine;

namespace Kudos.Network.Utils
{
    /// <summary>One second of traffic on one channel in one direction.</summary>
    public struct ChannelRate
    {
        public long BytesPerSecond;
        public int PacketsPerSecond;
        public float KilobitsPerSecond => BytesPerSecond * 8f / 1000f;
    }

    public static class NetworkStats
    {
        private const int ChannelCount = 3; // Reliable, StateSync, Voice

        // Accumulating window (written by transport threads).
        private static readonly long[] _inBytes = new long[ChannelCount];
        private static readonly long[] _outBytes = new long[ChannelCount];
        private static readonly int[] _inPackets = new int[ChannelCount];
        private static readonly int[] _outPackets = new int[ChannelCount];

        // Last completed 1s window (read by the overlay).
        private static readonly ChannelRate[] _inRate = new ChannelRate[ChannelCount];
        private static readonly ChannelRate[] _outRate = new ChannelRate[ChannelCount];

        private static double _windowStart = -1.0;
        private static readonly object _rollLock = new object();

        /// <summary>Record a received packet. Called by the transport for every inbound segment.</summary>
        public static void RecordIn(KudosChannel channel, int bytes)
        {
            int c = (int)channel;
            if ((uint)c >= ChannelCount) return;
            Interlocked.Add(ref _inBytes[c], bytes);
            Interlocked.Increment(ref _inPackets[c]);
            MaybeRoll();
        }

        /// <summary>Record a sent packet. Called by the transport for every outbound segment (per connection).</summary>
        public static void RecordOut(KudosChannel channel, int bytes)
        {
            int c = (int)channel;
            if ((uint)c >= ChannelCount) return;
            Interlocked.Add(ref _outBytes[c], bytes);
            Interlocked.Increment(ref _outPackets[c]);
            MaybeRoll();
        }

        /// <summary>Rate over the last completed second for an inbound channel.</summary>
        public static ChannelRate InRate(KudosChannel channel) { MaybeRoll(); return _inRate[(int)channel]; }

        /// <summary>Rate over the last completed second for an outbound channel.</summary>
        public static ChannelRate OutRate(KudosChannel channel) { MaybeRoll(); return _outRate[(int)channel]; }

        /// <summary>Total inbound kbps across all channels (last completed second).</summary>
        public static float TotalInKbps()
        {
            MaybeRoll();
            float t = 0f;
            for (int i = 0; i < ChannelCount; i++) t += _inRate[i].KilobitsPerSecond;
            return t;
        }

        /// <summary>Total outbound kbps across all channels (last completed second).</summary>
        public static float TotalOutKbps()
        {
            MaybeRoll();
            float t = 0f;
            for (int i = 0; i < ChannelCount; i++) t += _outRate[i].KilobitsPerSecond;
            return t;
        }

        /// <summary>Zero everything - call on room leave so stale rates don't linger.</summary>
        public static void Reset()
        {
            lock (_rollLock)
            {
                for (int i = 0; i < ChannelCount; i++)
                {
                    _inBytes[i] = _outBytes[i] = 0;
                    _inPackets[i] = _outPackets[i] = 0;
                    _inRate[i] = default;
                    _outRate[i] = default;
                }
                _windowStart = Now();
            }
        }

        // ---- window rolling -------------------------------------------------

        private static double Now()
        {
            // Time.realtimeSinceStartupAsDouble is main-thread-only in older
            // Unity versions; DateTime is safe from any thread and 1s windows
            // don't need sub-ms precision.
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }

        private static void MaybeRoll()
        {
            double now = Now();
            if (_windowStart < 0) { _windowStart = now; return; }
            if (now - _windowStart < 1.0) return;

            lock (_rollLock)
            {
                double elapsed = now - _windowStart;
                if (elapsed < 1.0) return; // another thread rolled first

                for (int i = 0; i < ChannelCount; i++)
                {
                    _inRate[i] = new ChannelRate
                    {
                        BytesPerSecond = (long)(Interlocked.Exchange(ref _inBytes[i], 0) / elapsed),
                        PacketsPerSecond = (int)(Interlocked.Exchange(ref _inPackets[i], 0) / elapsed),
                    };
                    _outRate[i] = new ChannelRate
                    {
                        BytesPerSecond = (long)(Interlocked.Exchange(ref _outBytes[i], 0) / elapsed),
                        PacketsPerSecond = (int)(Interlocked.Exchange(ref _outPackets[i], 0) / elapsed),
                    };
                }
                _windowStart = now;
            }
        }
    }
}
