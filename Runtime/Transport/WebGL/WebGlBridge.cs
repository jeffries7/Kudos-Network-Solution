// Kudos Network Solution (KNS)
// WebGL backends: browser-native RTCPeerConnection + WebSocket via .jslib interop.
// Paired with Plugins/WebGL/KudosWebRtc.jslib.

#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;

namespace Kudos.Network.Transport.WebGL
{
    internal sealed class BrowserRtcPeerConnection : IRtcPeerConnection
    {
        public float RttMs => KNS_RtcGetRtt(_handle);
        public event Action OnOpen;
        public event Action OnClosed;
        public event Action<string> OnIceCandidate;
        public event Action<KudosChannel, ArraySegment<byte>> OnMessage;

        private readonly int _handle;
        private static readonly Dictionary<int, BrowserRtcPeerConnection> Instances
            = new Dictionary<int, BrowserRtcPeerConnection>();
        private static Action<string> _pendingSdpCallback;

        public BrowserRtcPeerConnection(RtcConfig config)
        {
            _handle = KNS_RtcCreate(UnityEngine.JsonUtility.ToJson(config),
                OnOpenCb, OnCloseCb, OnIceCb, OnMessageCb, OnSdpCb);
            Instances[_handle] = this;
        }

        public void CreateDataChannel(KudosChannel channel, bool ordered, int maxRetransmits)
            => KNS_RtcCreateChannel(_handle, (int)channel, ordered, maxRetransmits);

        public void CreateOffer(Action<string> onSdp) { _pendingSdpCallback = onSdp; KNS_RtcCreateOffer(_handle); }
        public void CreateAnswer(Action<string> onSdp) { _pendingSdpCallback = onSdp; KNS_RtcCreateAnswer(_handle); }
        public void SetRemoteDescription(string sdp) => KNS_RtcSetRemote(_handle, sdp);
        public void AddIceCandidate(string candidateJson) => KNS_RtcAddIce(_handle, candidateJson);

        public void Send(KudosChannel channel, ArraySegment<byte> data)
        {
            unsafe
            {
                fixed (byte* ptr = &data.Array[data.Offset])
                    KNS_RtcSend(_handle, (int)channel, (IntPtr)ptr, data.Count);
            }
        }

        public void Poll() { /* browser callbacks arrive on the main JS thread already */ }
        public void Close() { KNS_RtcClose(_handle); Instances.Remove(_handle); }

        // ---- static trampolines (WebGL requires MonoPInvokeCallback on static methods)
        [MonoPInvokeCallback(typeof(Action<int>))]
        private static void OnOpenCb(int handle) { if (Instances.TryGetValue(handle, out var i)) i.OnOpen?.Invoke(); }

        [MonoPInvokeCallback(typeof(Action<int>))]
        private static void OnCloseCb(int handle) { if (Instances.TryGetValue(handle, out var i)) i.OnClosed?.Invoke(); }

        [MonoPInvokeCallback(typeof(Action<int, string>))]
        private static void OnIceCb(int handle, string json) { if (Instances.TryGetValue(handle, out var i)) i.OnIceCandidate?.Invoke(json); }

        [MonoPInvokeCallback(typeof(Action<int, string>))]
        private static void OnSdpCb(int handle, string sdp) { _pendingSdpCallback?.Invoke(sdp); _pendingSdpCallback = null; }

        [MonoPInvokeCallback(typeof(Action<int, int, IntPtr, int>))]
        private static void OnMessageCb(int handle, int channel, IntPtr ptr, int length)
        {
            if (!Instances.TryGetValue(handle, out var i)) return;
            var buffer = new byte[length];
            Marshal.Copy(ptr, buffer, 0, length);
            i.OnMessage?.Invoke((KudosChannel)channel, new ArraySegment<byte>(buffer));
        }

        // ---- jslib externs
        [DllImport("__Internal")] private static extern int  KNS_RtcCreate(string configJson, Action<int> onOpen, Action<int> onClose, Action<int,string> onIce, Action<int,int,IntPtr,int> onMsg, Action<int,string> onSdp);
        [DllImport("__Internal")] private static extern void KNS_RtcCreateChannel(int handle, int channel, bool ordered, int maxRetransmits);
        [DllImport("__Internal")] private static extern void KNS_RtcCreateOffer(int handle);
        [DllImport("__Internal")] private static extern void KNS_RtcCreateAnswer(int handle);
        [DllImport("__Internal")] private static extern void KNS_RtcSetRemote(int handle, string sdp);
        [DllImport("__Internal")] private static extern void KNS_RtcAddIce(int handle, string json);
        [DllImport("__Internal")] private static extern void KNS_RtcSend(int handle, int channel, IntPtr data, int length);
        [DllImport("__Internal")] private static extern void KNS_RtcClose(int handle);
        [DllImport("__Internal")] private static extern float KNS_RtcGetRtt(int handle);
    }

    /// <summary>Browser-native WebSocket for signaling.</summary>
    internal sealed class WebGlSignalingSocket : ISignalingSocket
    {
        public bool IsConnected { get; private set; }
        public event Action OnOpen;
        public event Action<string> OnTextMessage;
        public event Action<string> OnClose;

        private int _handle;
        private static WebGlSignalingSocket _instance; // one signaling socket per app

        public void Connect(string url)
        {
            _instance = this;
            _handle = KNS_WsConnect(url, WsOpenCb, WsMessageCb, WsCloseCb);
        }

        public void SendText(string text) => KNS_WsSend(_handle, text);
        public void Poll() { }
        public void Dispose() => KNS_WsClose(_handle);

        [MonoPInvokeCallback(typeof(Action))]
        private static void WsOpenCb() { if (_instance != null) { _instance.IsConnected = true; _instance.OnOpen?.Invoke(); } }

        [MonoPInvokeCallback(typeof(Action<string>))]
        private static void WsMessageCb(string text) => _instance?.OnTextMessage?.Invoke(text);

        [MonoPInvokeCallback(typeof(Action<string>))]
        private static void WsCloseCb(string reason) { if (_instance != null) { _instance.IsConnected = false; _instance.OnClose?.Invoke(reason); } }

        [DllImport("__Internal")] private static extern int  KNS_WsConnect(string url, Action onOpen, Action<string> onMsg, Action<string> onClose);
        [DllImport("__Internal")] private static extern void KNS_WsSend(int handle, string text);
        [DllImport("__Internal")] private static extern void KNS_WsClose(int handle);
    }
}
#endif
