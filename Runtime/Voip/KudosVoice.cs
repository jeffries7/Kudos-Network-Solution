// Kudos Network Solution (KNS)
// KudosVoice - integrated spatial VOIP.
//
// DESIGN LINEAGE (Photon Voice, rebuilt for our star topology): Opus frames
// over the unreliable Voice data channel. The host forwards frames SFU-style
// (no decode/re-encode - just relay), so:
//   * client uplink is ONE stream regardless of room size
//   * host uplink is the scaling cost: (N-1) listeners x (N-1) speakers worst
//     case, mitigated by (a) 16 kbps Opus, (b) VAD - silent players send
//     nothing, and in a social room most people listen most of the time,
//     (c) optional distance culling: the host skips forwarding to listeners
//     beyond earshot of the speaker.
//
// PIPELINE (per peer):
//   Mic (48kHz mono) -> 20ms frames -> VAD gate -> Opus encode (~16kbps)
//     -> VoiceFrame packet -> host relay -> JitterBuffer -> Opus decode
//     -> SpatialVoicePlayer (AudioSource at the speaker's avatar head)

using System;
using System.Collections.Generic;
using Kudos.Network.Serialization;
using Kudos.Network.Transport;
using UnityEngine;

namespace Kudos.Network.Voip
{
    [AddComponentMenu("Kudos/Kudos Voice")]
    public sealed class KudosVoice : MonoBehaviour
    {
        public static KudosVoice Instance { get; private set; }

        public const int SampleRate = 48000;
        public const int FrameMs = 20;
        public const int SamplesPerFrame = SampleRate * FrameMs / 1000; // 960

        [Header("Capture")]
        public bool MicEnabled = true;
        [Tooltip("Simple energy-gate voice activity detection. Frames below this RMS are not sent.")]
        [Range(0f, 0.1f)] public float VadThreshold = 0.01f;
        [Tooltip("Keep transmitting this long after the last voiced frame, so word endings aren't clipped.")]
        public float VadHangoverSeconds = 0.4f;

        [Header("Playback")]
        public SpatialVoicePlayer VoicePlayerPrefab;
        [Tooltip("Host stops forwarding a speaker to listeners further away than this. 0 = no culling.")]
        public float MaxHearingDistance = 25f;

        private MicrophoneCapture _mic;
        private IVoiceCodec _encoder;
        private readonly Dictionary<PlayerId, VoiceReceiver> _receivers = new Dictionary<PlayerId, VoiceReceiver>();
        private ushort _frameSequence;
        private float _lastVoicedTime;

        private sealed class VoiceReceiver
        {
            public IVoiceCodec Decoder;
            public JitterBuffer Jitter;
            public SpatialVoicePlayer Player;
        }

        private void Awake()
        {
            Instance = this;
            _encoder = VoiceCodecFactory.Create();
        }

        private void Start()
        {
            _mic = new MicrophoneCapture(SampleRate, SamplesPerFrame);
            _mic.OnFrame += HandleMicFrame;
            _mic.Start();

            var net = KudosNetworkManager.Instance;
            net.OnPlayerLeft += peer => RemoveReceiver(peer.PlayerId);
        }

        // ------------------------------------------------------------------ send path

        private void HandleMicFrame(float[] pcm)
        {
            var net = KudosNetworkManager.Instance;
            if (net == null || !net.IsConnected || !MicEnabled) return;

            // VAD with hangover
            if (Rms(pcm) >= VadThreshold) _lastVoicedTime = Time.unscaledTime;
            if (Time.unscaledTime - _lastVoicedTime > VadHangoverSeconds) return;

            int encodedLen = _encoder.Encode(pcm, _scratchEncode);

            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.VoiceFrame);
            w.WriteUShort(net.LocalPlayerId.Value);
            w.WriteUShort(_frameSequence++);
            w.WriteSegment(new ArraySegment<byte>(_scratchEncode, 0, encodedLen));
            net.SendToHostOrBroadcast(w.ToSegment(), KudosChannel.Voice);
            KudosWriter.Return(w);
        }

        private readonly byte[] _scratchEncode = new byte[512];

        private static float Rms(float[] samples)
        {
            float sum = 0f;
            for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
            return Mathf.Sqrt(sum / samples.Length);
        }

        // ------------------------------------------------------------------ receive path

        public void HandleVoicePacket(KudosReader reader, ArraySegment<byte> rawPacket, int fromConnectionId)
        {
            var net = KudosNetworkManager.Instance;
            var speaker = new PlayerId(reader.ReadUShort());
            ushort sequence = reader.ReadUShort();
            var opusData = reader.ReadSegment();

            // Host: SFU relay with optional distance culling.
            if (net.IsHost)
            {
                if (MaxHearingDistance > 0f)
                    RelayWithDistanceCulling(net, rawPacket, speaker, fromConnectionId);
                else
                    net.Transport.Broadcast(rawPacket, KudosChannel.Voice, exceptConnectionId: fromConnectionId);
            }

            if (speaker == net.LocalPlayerId) return; // don't play our own echo
            if (Moderation.KudosModeration.IsVoiceMuted(speaker)) return; // local mute/block - drop before decode

            var receiver = GetOrCreateReceiver(speaker);
            receiver.Jitter.Push(sequence, opusData);
        }

        private void RelayWithDistanceCulling(KudosNetworkManager net, ArraySegment<byte> packet,
            PlayerId speaker, int fromConnectionId)
        {
            var speakerHead = Components.KudosAvatar.GetHeadPosition(speaker);
            foreach (var peer in net.Peers.Values)
            {
                if (peer.IsLocal || peer.ConnectionId < 0 || peer.ConnectionId == fromConnectionId) continue;
                if (speakerHead.HasValue)
                {
                    var listenerHead = Components.KudosAvatar.GetHeadPosition(peer.PlayerId);
                    if (listenerHead.HasValue &&
                        (speakerHead.Value - listenerHead.Value).sqrMagnitude > MaxHearingDistance * MaxHearingDistance)
                        continue;
                }
                net.Transport.Send(peer.ConnectionId, packet, KudosChannel.Voice);
            }
        }

        private VoiceReceiver GetOrCreateReceiver(PlayerId speaker)
        {
            if (_receivers.TryGetValue(speaker, out var existing)) return existing;

            var player = VoicePlayerPrefab != null
                ? Instantiate(VoicePlayerPrefab)
                : new GameObject($"Voice_{speaker}").AddComponent<SpatialVoicePlayer>();
            player.Initialise(speaker);

            var receiver = new VoiceReceiver
            {
                Decoder = VoiceCodecFactory.Create(),
                Jitter = new JitterBuffer(),
                Player = player
            };
            _receivers[speaker] = receiver;
            return receiver;
        }

        private void RemoveReceiver(PlayerId speaker)
        {
            if (!_receivers.TryGetValue(speaker, out var r)) return;
            if (r.Player != null) Destroy(r.Player.gameObject);
            _receivers.Remove(speaker);
        }

        // ------------------------------------------------------------------ playback pump

        private readonly float[] _scratchDecode = new float[SamplesPerFrame];

        private void Update()
        {
            // Feed each receiver's audio player from its jitter buffer.
            foreach (var kvp in _receivers)
            {
                var r = kvp.Value;
                while (r.Player.WantsMoreAudio)
                {
                    if (r.Jitter.TryPop(out var frame, out bool lost))
                    {
                        int samples = lost
                            ? r.Decoder.DecodeLost(_scratchDecode)   // Opus PLC - conceal the gap
                            : r.Decoder.Decode(frame, _scratchDecode);
                        r.Player.PushSamples(_scratchDecode, samples);
                    }
                    else break; // buffer warming up
                }
            }
        }

        private void OnDestroy()
        {
            _mic?.Stop();
            if (Instance == this) Instance = null;
        }
    }
}
