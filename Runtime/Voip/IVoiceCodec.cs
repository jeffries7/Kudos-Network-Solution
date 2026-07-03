// Kudos Network Solution (KNS)
// Voice codec abstraction.
//
// PRODUCTION CODEC: Opus, mono, 48kHz, ~16kbps VBR, complexity 5.
// Recommended integration: Concentus (pure-C# Opus port, MIT licensed) -
// it runs unmodified on IL2CPP, Quest AND WebGL, which native libopus
// P/Invoke cannot (no DllImport of shared libs in a browser). If native
// performance is ever needed on Quest, swap in a libopus binding behind
// this same interface for Android only.
//
//   Encoder: OpusEncoder(48000, 1, OPUS_APPLICATION_VOIP), Bitrate = 16000
//   Decoder: OpusDecoder(48000, 1); PLC via Decode(null, ...) on loss.

using System;
using UnityEngine;

namespace Kudos.Network.Voip
{
    public interface IVoiceCodec
    {
        /// <summary>Encode one PCM frame. Returns encoded byte count written to output.</summary>
        int Encode(float[] pcm, byte[] output);

        /// <summary>Decode one frame into pcm. Returns sample count.</summary>
        int Decode(ArraySegment<byte> encoded, float[] pcm);

        /// <summary>Packet-loss concealment: synthesize a plausible frame for a lost packet.</summary>
        int DecodeLost(float[] pcm);
    }

    public static class VoiceCodecFactory
    {
        /// <summary>Swap point for the real Opus codec. See file header.</summary>
        public static IVoiceCodec Create()
        {
            // TODO(integration): return new ConcentusOpusCodec();
            return new PassthroughCodec();
        }
    }

    /// <summary>
    /// Uncompressed 16-bit PCM "codec" for editor/dev-loop testing (works end to
    /// end but at 768 kbps - fine on localhost, NOT for production rooms).
    /// Logs a one-time warning so it can never sneak into a release unnoticed.
    /// </summary>
    internal sealed class PassthroughCodec : IVoiceCodec
    {
        private static bool _warned;
        private readonly float[] _lastFrame = new float[KudosVoice.SamplesPerFrame];

        public PassthroughCodec()
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning("[KNS] Using PassthroughCodec (uncompressed voice, ~768kbps/speaker). Integrate Opus (Concentus) before shipping.");
        }

        public int Encode(float[] pcm, byte[] output)
        {
            int n = Mathf.Min(pcm.Length, output.Length / 2);
            for (int i = 0; i < n; i++)
            {
                short s = (short)Mathf.Clamp(pcm[i] * 32767f, short.MinValue, short.MaxValue);
                output[i * 2] = (byte)s;
                output[i * 2 + 1] = (byte)(s >> 8);
            }
            return n * 2;
        }

        public int Decode(ArraySegment<byte> encoded, float[] pcm)
        {
            int n = Mathf.Min(encoded.Count / 2, pcm.Length);
            for (int i = 0; i < n; i++)
            {
                short s = (short)(encoded.Array[encoded.Offset + i * 2]
                                | encoded.Array[encoded.Offset + i * 2 + 1] << 8);
                pcm[i] = s / 32768f;
                _lastFrame[i] = pcm[i];
            }
            return n;
        }

        public int DecodeLost(float[] pcm)
        {
            // Crude PLC: replay last frame at reduced gain (real Opus does far better).
            for (int i = 0; i < pcm.Length; i++) pcm[i] = _lastFrame[i] * 0.5f;
            return pcm.Length;
        }
    }
}
