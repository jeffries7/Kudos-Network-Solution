// Kudos Network Solution (KNS)
// Transport abstraction.
//
// DESIGN LINEAGE (Mirror): Mirror's greatest architectural strength is its
// transport abstraction - the sim layer never knows what wire it runs on.
// KNS adopts this, but bakes in three QoS channels (WebRTC data channels map
// to these 1:1), because Fusion/Coherence proved that state, control and
// voice traffic have fundamentally different delivery requirements.

using System;

namespace Kudos.Network.Transport
{
    /// <summary>Delivery guarantees. Each maps to a dedicated WebRTC data channel (or SCTP stream).</summary>
    public enum KudosChannel : byte
    {
        /// <summary>Reliable + ordered. Spawns, despawns, RPCs, authority transfer, room control.</summary>
        Reliable = 0,
        /// <summary>Unreliable + sequenced (stale packets dropped on arrival). State snapshots.</summary>
        StateSync = 1,
        /// <summary>Unreliable, unordered. VOIP frames - the jitter buffer handles reordering.</summary>
        Voice = 2,
    }

    /// <summary>Connection-level events surfaced to the layer above.</summary>
    public enum TransportEvent : byte
    {
        Connected,
        Data,
        Disconnected,
    }

    /// <summary>
    /// A transport moves opaque byte segments between this peer and remote peers,
    /// identified by connection ids. The host holds N connections; a client holds
    /// exactly one (id 0 = the host).
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>True while this transport is acting as the room host (listen server).</summary>
        bool IsHost { get; }

        bool IsRunning { get; }

        /// <summary>Fired from PollEvents on the main thread.</summary>
        event Action<int> OnPeerConnected;                       // connectionId
        event Action<int, ArraySegment<byte>, KudosChannel> OnData; // connectionId, payload, channel
        event Action<int, DisconnectReason> OnPeerDisconnected;  // connectionId

        /// <summary>Start hosting. Registers with signaling so joiners can find us.</summary>
        void StartHost(string roomId);

        /// <summary>Connect to the host of the given room via signaling.</summary>
        void Connect(string roomId, string hostPeerId);

        void Send(int connectionId, ArraySegment<byte> payload, KudosChannel channel);

        /// <summary>Host convenience: send to every connection except one (or -1 for all).</summary>
        void Broadcast(ArraySegment<byte> payload, KudosChannel channel, int exceptConnectionId = -1);

        void Disconnect(int connectionId);
        void Shutdown();

        /// <summary>Pump the event queue on Unity's main thread. Call once per frame.</summary>
        void PollEvents();

        /// <summary>Round-trip time estimate in ms for a connection (from WebRTC stats / ping).</summary>
        float GetRttMs(int connectionId);
    }

    public enum DisconnectReason : byte
    {
        Normal = 0,
        Timeout = 1,
        TransportError = 2,
        Kicked = 3,
        HostMigrating = 4,
    }
}
