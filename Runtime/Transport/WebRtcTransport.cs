// Kudos Network Solution (KNS)
// WebRTC transport - one wire protocol for every Kudos platform.
//
// WHY WEBRTC EVERYWHERE:
//   * WebGL cannot open UDP sockets; WebRTC data channels are the ONLY way to
//     get unreliable low-latency delivery in a browser.
//   * Rather than maintaining a UDP path for native and a WebRTC path for web
//     (and dealing with cross-play between them), KNS runs WebRTC data channels
//     on ALL platforms. Desktop <-> Quest <-> Browser peers interoperate freely.
//   * NAT traversal (STUN/TURN + ICE) comes for free, which pure UDP P2P would
//     need us to build by hand.
//
// TOPOLOGY: star, not mesh. The host holds one RTCPeerConnection per client.
// A 31-client mesh would be 465 connections and unworkable on Quest; a star
// is 31 on the host and 1 per client. Voice is forwarded by the host (SFU-style)
// so client uplink stays flat regardless of room size.
//
// PLATFORM BACKENDS:
//   * Windows / macOS / Android(Quest): com.unity.webrtc (Unity's WebRTC package)
//   * WebGL: the browser's native RTCPeerConnection via Plugins/WebGL/KudosWebRtc.jslib
// Both are hidden behind IRtcPeerConnection so this file has zero #if noise in
// its core logic.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kudos.Network.Transport
{
    public sealed class WebRtcTransport : ITransport
    {
        public bool IsHost { get; private set; }
        public bool IsRunning { get; private set; }

        public event Action<int> OnPeerConnected;
        public event Action<int, ArraySegment<byte>, KudosChannel> OnData;
        public event Action<int, DisconnectReason> OnPeerDisconnected;

        private readonly SignalingClient _signaling;
        private readonly RtcConfig _config;
        private string _roomId;

        // connectionId -> peer connection wrapper
        private readonly Dictionary<int, IRtcPeerConnection> _connections = new Dictionary<int, IRtcPeerConnection>();
        private readonly Dictionary<string, int> _peerIdToConnection = new Dictionary<string, int>();
        private int _nextConnectionId = 1; // 0 is reserved: "the host" from a client's perspective

        public WebRtcTransport(SignalingClient signaling, RtcConfig config = null)
        {
            _signaling = signaling;
            _config = config ?? RtcConfig.Default;
            _signaling.OnMessage += HandleSignal;
        }

        // ------------------------------------------------------------------ lifecycle

        public void StartHost(string roomId)
        {
            _roomId = roomId;
            IsHost = true;
            IsRunning = true;
            // Host is passive: it waits for "offer" signals from joiners (see HandleSignal).
        }

        public void Connect(string roomId, string hostPeerId)
        {
            _roomId = roomId;
            IsHost = false;
            IsRunning = true;

            var pc = CreatePeerConnection(0, hostPeerId, initiator: true);
            pc.CreateOffer(sdp => _signaling.Send(new SignalMessage
            {
                type = "offer", to = hostPeerId, roomId = roomId, payload = sdp
            }));
        }

        public void Shutdown()
        {
            foreach (var pc in _connections.Values) pc.Close();
            _connections.Clear();
            _peerIdToConnection.Clear();
            IsRunning = false;
        }

        public void Dispose() => Shutdown();

        // ------------------------------------------------------------------ signaling glue

        private void HandleSignal(SignalMessage msg)
        {
            switch (msg.type)
            {
                case "offer" when IsHost:
                {
                    // A new joiner wants in. Create their connection, answer.
                    int connId = _nextConnectionId++;
                    var pc = CreatePeerConnection(connId, msg.from, initiator: false);
                    pc.SetRemoteDescription(msg.payload);
                    pc.CreateAnswer(sdp => _signaling.Send(new SignalMessage
                    {
                        type = "answer", to = msg.from, roomId = _roomId, payload = sdp
                    }));
                    break;
                }
                case "answer" when !IsHost:
                    if (_connections.TryGetValue(0, out var hostPc))
                        hostPc.SetRemoteDescription(msg.payload);
                    break;

                case "ice":
                    if (_peerIdToConnection.TryGetValue(msg.from, out var id) &&
                        _connections.TryGetValue(id, out var target))
                        target.AddIceCandidate(msg.payload);
                    break;
            }
        }

        private IRtcPeerConnection CreatePeerConnection(int connectionId, string remotePeerId, bool initiator)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            IRtcPeerConnection pc = new WebGL.BrowserRtcPeerConnection(_config);
#else
            IRtcPeerConnection pc = new UnityRtcPeerConnection(_config);
#endif
            _connections[connectionId] = pc;
            _peerIdToConnection[remotePeerId] = connectionId;

            pc.OnIceCandidate += candidateJson => _signaling.Send(new SignalMessage
            {
                type = "ice", to = remotePeerId, roomId = _roomId, payload = candidateJson
            });
            pc.OnOpen += () => OnPeerConnected?.Invoke(connectionId);
            pc.OnMessage += (channel, data) =>
            {
                Utils.NetworkStats.RecordIn(channel, data.Count);
                OnData?.Invoke(connectionId, data, channel);
            };
            pc.OnClosed += () =>
            {
                _connections.Remove(connectionId);
                _peerIdToConnection.Remove(remotePeerId);
                OnPeerDisconnected?.Invoke(connectionId, DisconnectReason.Timeout);
            };

            // The initiator (joiner) creates the three data channels; they are
            // negotiated in-band and pop out on the host side automatically.
            if (initiator)
            {
                pc.CreateDataChannel(KudosChannel.Reliable,  ordered: true,  maxRetransmits: -1);
                pc.CreateDataChannel(KudosChannel.StateSync, ordered: false, maxRetransmits: 0);
                pc.CreateDataChannel(KudosChannel.Voice,     ordered: false, maxRetransmits: 0);
            }
            return pc;
        }

        // ------------------------------------------------------------------ data plane

        public void Send(int connectionId, ArraySegment<byte> payload, KudosChannel channel)
        {
            if (_connections.TryGetValue(connectionId, out var pc))
            {
                Utils.NetworkStats.RecordOut(channel, payload.Count);
                pc.Send(channel, payload);
            }
        }

        public void Broadcast(ArraySegment<byte> payload, KudosChannel channel, int exceptConnectionId = -1)
        {
            foreach (var kvp in _connections)
                if (kvp.Key != exceptConnectionId)
                {
                    Utils.NetworkStats.RecordOut(channel, payload.Count);
                    kvp.Value.Send(channel, payload);
                }
        }

        public void Disconnect(int connectionId)
        {
            if (_connections.TryGetValue(connectionId, out var pc)) pc.Close();
        }

        public void PollEvents()
        {
            _signaling.PollEvents();
            foreach (var pc in _connections.Values) pc.Poll();
        }

        public float GetRttMs(int connectionId)
            => _connections.TryGetValue(connectionId, out var pc) ? pc.RttMs : -1f;
    }

    /// <summary>ICE server configuration. Point these at your self-hosted coturn instance.</summary>
    [Serializable]
    public class RtcConfig
    {
        public string[] stunUrls = { "stun:stun.l.google.com:19302" };
        public string turnUrl = "";       // e.g. "turn:turn.kudos.example.com:3478"
        public string turnUsername = "";
        public string turnCredential = "";

        public static RtcConfig Default => new RtcConfig();
    }

    /// <summary>
    /// Minimal peer-connection surface both backends implement.
    /// UnityRtcPeerConnection (com.unity.webrtc) and BrowserRtcPeerConnection (.jslib).
    /// </summary>
    public interface IRtcPeerConnection
    {
        float RttMs { get; }
        event Action OnOpen;                                    // all data channels open
        event Action OnClosed;
        event Action<string> OnIceCandidate;                    // candidate json to relay
        event Action<KudosChannel, ArraySegment<byte>> OnMessage;

        void CreateDataChannel(KudosChannel channel, bool ordered, int maxRetransmits);
        void CreateOffer(Action<string> onSdp);
        void CreateAnswer(Action<string> onSdp);
        void SetRemoteDescription(string sdp);
        void AddIceCandidate(string candidateJson);
        void Send(KudosChannel channel, ArraySegment<byte> data);
        void Poll();
        void Close();
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    /// <summary>
    /// Native backend built on com.unity.webrtc (Unity.WebRTC namespace).
    /// SKELETON: the calls below map 1:1 onto Unity.WebRTC.RTCPeerConnection;
    /// wire-up is left as integration work so this package compiles without the
    /// dependency present. See README "Integration checklist".
    /// </summary>
    internal sealed class UnityRtcPeerConnection : IRtcPeerConnection
    {
        public float RttMs { get; private set; }
        public event Action OnOpen;
        public event Action OnClosed;
        public event Action<string> OnIceCandidate;
        public event Action<KudosChannel, ArraySegment<byte>> OnMessage;

        public UnityRtcPeerConnection(RtcConfig config)
        {
            // TODO(integration): new RTCPeerConnection(ref RTCConfiguration { iceServers = ... })
            //   pc.OnIceCandidate      -> OnIceCandidate(JsonUtility.ToJson(candidate))
            //   pc.OnDataChannel       -> AttachChannel(dc)  (host side)
            //   dc.OnOpen (all three)  -> OnOpen()
            //   dc.OnMessage           -> OnMessage(channelOf(dc), data)
            //   periodic pc.GetStats() -> RttMs
            Debug.LogWarning("[KNS] UnityRtcPeerConnection is a skeleton - install com.unity.webrtc and complete the TODOs.");
        }

        public void CreateDataChannel(KudosChannel channel, bool ordered, int maxRetransmits) { /* TODO */ }
        public void CreateOffer(Action<string> onSdp) { /* TODO: pc.CreateOffer -> SetLocalDescription -> onSdp */ }
        public void CreateAnswer(Action<string> onSdp) { /* TODO */ }
        public void SetRemoteDescription(string sdp) { /* TODO */ }
        public void AddIceCandidate(string candidateJson) { /* TODO */ }
        public void Send(KudosChannel channel, ArraySegment<byte> data) { /* TODO: dc.Send */ }
        public void Poll() { /* com.unity.webrtc dispatches via WebRTC.Update() coroutine */ }
        public void Close() { OnClosed?.Invoke(); }
    }
#endif
}
