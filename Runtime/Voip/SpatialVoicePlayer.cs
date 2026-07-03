// Kudos Network Solution (KNS)
// Spatial voice playback.
//
// Decoded PCM goes into a ring buffer consumed by OnAudioFilterRead on the
// audio thread. The AudioSource is fully 3D (spatialBlend = 1) and the
// GameObject follows the speaker's avatar head every frame, so voices come
// from mouths - essential presence for social VR. Works with any Unity
// spatializer plugin (Meta XR Audio, Steam Audio) transparently.

using Kudos.Network.Components;
using UnityEngine;

namespace Kudos.Network.Voip
{
    [RequireComponent(typeof(AudioSource))]
    public sealed class SpatialVoicePlayer : MonoBehaviour
    {
        public PlayerId Speaker { get; private set; }

        [Tooltip("Ring buffer size in seconds. Bigger = safer against stalls, more memory.")]
        public float BufferSeconds = 1f;

        [Tooltip("Keep roughly this many seconds queued; the Update pump uses WantsMoreAudio to hold it.")]
        public float TargetQueueSeconds = 0.08f; // 80ms: jitter latency + a safety margin

        private float[] _ring;
        private int _writePos;
        private int _readPos;
        private volatile int _queued;

        private AudioSource _source;

        /// <summary>True while the queue is below target - KudosVoice pumps frames in response.</summary>
        public bool WantsMoreAudio => _queued < (int)(TargetQueueSeconds * KudosVoice.SampleRate);

        public void Initialise(PlayerId speaker)
        {
            Speaker = speaker;
            _ring = new float[(int)(BufferSeconds * KudosVoice.SampleRate)];

            _source = GetComponent<AudioSource>();
            if (_source == null) _source = gameObject.AddComponent<AudioSource>();
            _source.spatialBlend = 1f;                       // fully 3D
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.minDistance = 1f;
            _source.maxDistance = 25f;                       // match KudosVoice.MaxHearingDistance
            _source.spatialize = true;                       // hand off to the active VR spatializer
            _source.loop = true;
            // Silent looping clip; actual samples are injected in OnAudioFilterRead.
            _source.clip = AudioClip.Create($"VoiceFeed_{speaker}", KudosVoice.SampleRate, 1, KudosVoice.SampleRate, false);
            _source.Play();
        }

        /// <summary>Main thread: queue decoded samples.</summary>
        public void PushSamples(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _ring[_writePos] = samples[i];
                _writePos = (_writePos + 1) % _ring.Length;
            }
            System.Threading.Interlocked.Add(ref _queued, count);
        }

        /// <summary>Audio thread: drain the ring into the output buffer.</summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_ring == null) return;
            int frames = data.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                float sample = 0f;
                if (_queued > 0)
                {
                    sample = _ring[_readPos];
                    _readPos = (_readPos + 1) % _ring.Length;
                    System.Threading.Interlocked.Decrement(ref _queued);
                }
                for (int c = 0; c < channels; c++)
                    data[f * channels + c] = sample; // mono voice to all channels; spatializer pans
            }
        }

        private void LateUpdate()
        {
            // Follow the speaker's head so the voice is positioned at their mouth.
            var head = KudosAvatar.GetHeadPosition(Speaker);
            if (head.HasValue) transform.position = head.Value;
        }
    }
}
