// Kudos Network Solution (KNS)
// Microphone capture -> fixed-size PCM frames.
//
// Native (Windows / macOS / Quest): Unity's Microphone API into a looping
// AudioClip, drained on Update into exact 960-sample (20ms @ 48kHz) frames.
//
// WebGL: Unity's Microphone API does not exist in browsers. Production path is
// a getUserMedia + AudioWorklet .jslib (mirroring KudosWebRtc.jslib patterns)
// pushing frames into the same OnFrame event. Stubbed here with a clear error.

using System;
using UnityEngine;

namespace Kudos.Network.Voip
{
    public sealed class MicrophoneCapture
    {
        /// <summary>Fires with a full frame of mono PCM floats (length == samplesPerFrame).</summary>
        public event Action<float[]> OnFrame;

        public bool IsCapturing { get; private set; }

        private readonly int _sampleRate;
        private readonly int _samplesPerFrame;
        private readonly float[] _frame;

        private AudioClip _micClip;
        private string _device;
        private int _readHead;
        private GameObject _pump;

        public MicrophoneCapture(int sampleRate, int samplesPerFrame)
        {
            _sampleRate = sampleRate;
            _samplesPerFrame = samplesPerFrame;
            _frame = new float[samplesPerFrame];
        }

        public void Start()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogError("[KNS] WebGL microphone capture requires the getUserMedia .jslib bridge - see README 'Integration checklist'.");
#else
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[KNS] No microphone found - voice send disabled.");
                return;
            }
            _device = Microphone.devices[0];
            // 1-second looping clip; we chase the write head.
            _micClip = Microphone.Start(_device, loop: true, lengthSec: 1, frequency: _sampleRate);
            _readHead = 0;
            IsCapturing = true;

            // Hidden pump MonoBehaviour so a plain class can receive Update().
            _pump = new GameObject("KNS_MicPump") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_pump);
            _pump.AddComponent<MicPump>().Capture = this;
#endif
        }

        public void Stop()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            if (IsCapturing) Microphone.End(_device);
            if (_pump != null) UnityEngine.Object.Destroy(_pump);
#endif
            IsCapturing = false;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        internal void Pump()
        {
            if (!IsCapturing || _micClip == null) return;

            int writeHead = Microphone.GetPosition(_device);
            int available = writeHead - _readHead;
            if (available < 0) available += _micClip.samples; // ring wrap

            while (available >= _samplesPerFrame)
            {
                _micClip.GetData(_frame, _readHead);
                _readHead = (_readHead + _samplesPerFrame) % _micClip.samples;
                available -= _samplesPerFrame;
                OnFrame?.Invoke(_frame);
            }
        }

        private sealed class MicPump : MonoBehaviour
        {
            public MicrophoneCapture Capture;
            private void Update() => Capture?.Pump();
        }
#endif
    }
}
