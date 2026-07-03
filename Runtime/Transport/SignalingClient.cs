// Kudos Network Solution (KNS)
// WebSocket signaling client for the self-hosted "Nexus" backend.
//
// Responsibilities:
//   1. WebRTC signaling relay (SDP offers/answers + ICE candidates between peers)
//   2. Room registry RPCs (join-or-create, heartbeat, host migration notification)
//
// The wire format is line-delimited JSON - deliberately trivial so the Node.js
// backend (Backend/server.js) stays under a few hundred lines and any team
// member can read it end to end.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kudos.Network.Transport
{
    [Serializable]
    public class SignalMessage
    {
        public string type;      // "hello","join-or-create","room-assigned","offer","answer","ice",
                                 // "peer-joined","peer-left","host-changed","heartbeat","error"
        public string from;      // sender peerId
        public string to;        // target peerId ("" = server)
        public string roomId;
        public string sceneKey;  // which scene the room hosts (fill-or-create key)
        public string payload;   // SDP / ICE json / error text
        public int    maxPlayers;
        public bool   isHost;    // in "room-assigned": did we become the host of a fresh room?
        public string hostPeerId;
        public string[] peers;   // current occupants (room-assigned / host-changed)
    }

    /// <summary>
    /// Thin async WebSocket wrapper. Uses System.Net.WebSockets on native platforms
    /// and a .jslib bridge on WebGL (browsers expose WebSocket natively).
    /// </summary>
    public sealed class SignalingClient : IDisposable
    {
        public string PeerId { get; private set; }
        public bool IsConnected => _impl != null && _impl.IsConnected;

        public event Action<SignalMessage> OnMessage;
        public event Action OnConnected;
        public event Action<string> OnClosed;

        private ISignalingSocket _impl;
        private readonly Queue<SignalMessage> _inbox = new Queue<SignalMessage>();
        private readonly object _lock = new object();

        public void Connect(string url)
        {
            PeerId = Guid.NewGuid().ToString("N").Substring(0, 12);
#if UNITY_WEBGL && !UNITY_EDITOR
            _impl = new WebGL.WebGlSignalingSocket();
#else
            _impl = new NativeSignalingSocket();
#endif
            _impl.OnTextMessage += raw =>
            {
                var msg = JsonUtility.FromJson<SignalMessage>(raw);
                lock (_lock) _inbox.Enqueue(msg);
            };
            _impl.OnOpen += () =>
            {
                Send(new SignalMessage { type = "hello", from = PeerId });
                OnConnected?.Invoke();
            };
            _impl.OnClose += reason => OnClosed?.Invoke(reason);
            _impl.Connect(url);
        }

        public void Send(SignalMessage msg)
        {
            msg.from = PeerId;
            _impl?.SendText(JsonUtility.ToJson(msg));
        }

        /// <summary>Drain queued messages on the main thread. Called by the transport each frame.</summary>
        public void PollEvents()
        {
            _impl?.Poll();
            while (true)
            {
                SignalMessage msg;
                lock (_lock)
                {
                    if (_inbox.Count == 0) break;
                    msg = _inbox.Dequeue();
                }
                OnMessage?.Invoke(msg);
            }
        }

        public void Dispose() => _impl?.Dispose();
    }

    /// <summary>Platform-specific raw WebSocket. Implementations: NativeSignalingSocket, WebGlSignalingSocket.</summary>
    internal interface ISignalingSocket : IDisposable
    {
        bool IsConnected { get; }
        event Action OnOpen;
        event Action<string> OnTextMessage;
        event Action<string> OnClose;
        void Connect(string url);
        void SendText(string text);
        void Poll();
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    /// <summary>System.Net.WebSockets implementation for Windows / macOS / Android (Quest).</summary>
    internal sealed class NativeSignalingSocket : ISignalingSocket
    {
        public bool IsConnected => _ws != null && _ws.State == System.Net.WebSockets.WebSocketState.Open;
        public event Action OnOpen;
        public event Action<string> OnTextMessage;
        public event Action<string> OnClose;

        private System.Net.WebSockets.ClientWebSocket _ws;
        private System.Threading.CancellationTokenSource _cts;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThread
            = new System.Collections.Concurrent.ConcurrentQueue<Action>();

        public async void Connect(string url)
        {
            _ws = new System.Net.WebSockets.ClientWebSocket();
            _cts = new System.Threading.CancellationTokenSource();
            try
            {
                await _ws.ConnectAsync(new Uri(url), _cts.Token);
                _mainThread.Enqueue(() => OnOpen?.Invoke());
                _ = ReceiveLoop();
            }
            catch (Exception e)
            {
                _mainThread.Enqueue(() => OnClose?.Invoke(e.Message));
            }
        }

        private async System.Threading.Tasks.Task ReceiveLoop()
        {
            var buffer = new byte[16 * 1024];
            var sb = new System.Text.StringBuilder();
            try
            {
                while (_ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    sb.Clear();
                    System.Net.WebSockets.WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        {
                            _mainThread.Enqueue(() => OnClose?.Invoke("closed by server"));
                            return;
                        }
                        sb.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    var text = sb.ToString();
                    _mainThread.Enqueue(() => OnTextMessage?.Invoke(text));
                }
            }
            catch (Exception e)
            {
                _mainThread.Enqueue(() => OnClose?.Invoke(e.Message));
            }
        }

        public async void SendText(string text)
        {
            if (!IsConnected) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, _cts.Token);
            }
            catch { /* surfaced via receive loop close */ }
        }

        public void Poll()
        {
            while (_mainThread.TryDequeue(out var action)) action();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _ws?.Dispose();
        }
    }
#endif
}
