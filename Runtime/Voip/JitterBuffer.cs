// Kudos Network Solution (KNS)
// Voice jitter buffer.
//
// The Voice channel is unreliable AND unordered, so frames arrive early, late,
// duplicated or never. The jitter buffer re-orders by sequence number and
// releases frames at a steady cadence, trading a small fixed latency
// (WarmupFrames * 20ms) for smooth playback. Missing slots come out flagged
// `lost` so the decoder can run packet-loss concealment instead of clicking.

using System;
using System.Collections.Generic;

namespace Kudos.Network.Voip
{
    public sealed class JitterBuffer
    {
        /// <summary>Frames buffered before playback starts. 3 x 20ms = 60ms of protection.</summary>
        public int WarmupFrames = 3;

        /// <summary>Give up waiting for a frame this far behind the newest arrival.</summary>
        public int MaxLagFrames = 10;

        private readonly Dictionary<ushort, byte[]> _frames = new Dictionary<ushort, byte[]>();
        private ushort _playSequence;
        private ushort _newestSequence;
        private bool _started;

        public void Push(ushort sequence, ArraySegment<byte> data)
        {
            if (_started && !IsNewer(sequence, _playSequence)) return; // too late, already played past it
            if (_frames.ContainsKey(sequence)) return;                  // duplicate

            var copy = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);
            _frames[sequence] = copy;

            if (!_started || IsNewer(sequence, _newestSequence)) _newestSequence = sequence;

            if (!_started && _frames.Count >= WarmupFrames)
            {
                // Start playback at the oldest buffered frame.
                _playSequence = _newestSequence;
                foreach (var seq in _frames.Keys)
                    if (!IsNewer(seq, _playSequence)) _playSequence = seq;
                _started = true;
            }
        }

        /// <summary>
        /// Pop the next frame in sequence. Returns false when the buffer has nothing
        /// to offer yet (warming up / drained). `lost` true = synthesize via PLC.
        /// </summary>
        public bool TryPop(out ArraySegment<byte> frame, out bool lost)
        {
            frame = default;
            lost = false;
            if (!_started) return false;

            // Nothing newer than the play head buffered -> drained; wait.
            if (_frames.Count == 0 && !IsNewer(_newestSequence, _playSequence)) return false;

            if (_frames.TryGetValue(_playSequence, out var data))
            {
                _frames.Remove(_playSequence);
                frame = new ArraySegment<byte>(data);
                _playSequence++;
                return true;
            }

            // Frame missing. If the stream has moved well past it, emit a loss;
            // otherwise wait one more pump for late arrival.
            int lag = (ushort)(_newestSequence - _playSequence);
            if (lag > 0)
            {
                lost = true;
                _playSequence++;
                if (lag > MaxLagFrames) Resync();
                return true;
            }
            return false;
        }

        private void Resync()
        {
            // Fell too far behind (long stall) - jump to newest and rebuild latency.
            _frames.Clear();
            _started = false;
        }

        private static bool IsNewer(ushort a, ushort b) => (ushort)(a - b) < 32768;
    }
}
