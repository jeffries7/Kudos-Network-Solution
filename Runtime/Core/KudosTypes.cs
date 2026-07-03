// Kudos Network Solution (KNS)
// Shared core types: peers, authority, wire message ids.
//
// AUTHORITY MODEL (Coherence / Fusion "shared mode" hybrid):
// KNS uses DISTRIBUTED AUTHORITY. Every networked object has exactly one
// authority peer. The authority simulates the object and sends its state; the
// host relays but does not simulate other peers' objects. This suits a social
// platform perfectly:
//   * zero input latency on everything you touch (your avatar, grabbed props)
//   * host CPU stays light -> any Quest can host a 32-player room
//   * no anti-cheat requirement means we don't need server authority
// The HOST retains ARBITRATION authority: it resolves competing authority
// requests (two people grab the same prop) and owns scene-lifetime objects.

using System;

namespace Kudos.Network
{
    /// <summary>Stable identity of a peer within a room. The host is always PlayerId 0 by convention... 
    /// until host migration, where ids persist and HostId changes.</summary>
    [Serializable]
    public struct PlayerId : IEquatable<PlayerId>
    {
        public ushort Value;
        public PlayerId(ushort value) { Value = value; }

        public static readonly PlayerId None = new PlayerId(ushort.MaxValue);
        public bool IsValid => Value != ushort.MaxValue;

        public bool Equals(PlayerId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PlayerId other && Equals(other);
        public override int GetHashCode() => Value;
        public override string ToString() => IsValid ? $"Player{Value}" : "None";
        public static bool operator ==(PlayerId a, PlayerId b) => a.Value == b.Value;
        public static bool operator !=(PlayerId a, PlayerId b) => a.Value != b.Value;
    }

    /// <summary>Network-wide unique object id. Assigned by the host on spawn.</summary>
    [Serializable]
    public struct NetworkId : IEquatable<NetworkId>
    {
        public uint Value;
        public NetworkId(uint value) { Value = value; }
        public static readonly NetworkId None = new NetworkId(0);
        public bool IsValid => Value != 0;

        public bool Equals(NetworkId other) => Value == other.Value;
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"Net#{Value}";
    }

    /// <summary>Runtime info about a connected peer.</summary>
    public sealed class KudosPeer
    {
        public PlayerId PlayerId;
        public string SignalingPeerId;   // Nexus/WebRTC identity - survives host migration
        public int ConnectionId = -1;    // transport handle; -1 when not directly connected (client<->client)
        public string DisplayName = "";
        public float RttMs;
        public int JoinOrder;            // monotonically increasing; host-migration election key
        public bool IsLocal;
        public bool IsHost;

        // ---- reconnect grace (see KudosNetworkManager.ReconnectGraceSeconds) ----
        /// <summary>Link dropped; host is holding their state for the grace window.</summary>
        public bool IsAway;
        /// <summary>Host-side: secret handed out in Welcome; proves identity on resume.</summary>
        public uint ResumeToken;
        /// <summary>Host-side: realtimeSinceStartup after which an away peer is evicted.</summary>
        public float AwayDeadline;
    }

    /// <summary>Top-level message ids on the wire (first byte of every packet).</summary>
    public enum MsgId : byte
    {
        // Reliable channel
        Welcome = 1,            // host -> joiner: your PlayerId, peer roster, tick info
        PeerJoined = 2,         // host -> all
        PeerLeft = 3,           // host -> all
        Spawn = 4,              // authority -> host -> all (full initial state)
        Despawn = 5,
        Rpc = 6,
        AuthorityRequest = 7,   // client -> host: "I want authority over Net#X"
        AuthorityGranted = 8,   // host -> all: "Net#X authority is now PlayerN"
        AuthorityDenied = 9,    // host -> requester
        FullStateSync = 10,     // host -> late joiner: everything (also host-migration seed)
        HostMigration = 11,     // new host -> all: "I'm in charge now, state epoch N"
        Ping = 12,
        Pong = 13,
        Kick = 14,              // host -> target: removed from the room (reason string)
        PeerAway = 15,          // host -> all: peer link dropped, in reconnect grace window
        PeerResumed = 16,       // host -> all: away peer reconnected (same PlayerId)

        // StateSync channel (unreliable, sequenced)
        StateDelta = 20,        // dirty SyncVars + transforms for objects the sender has authority over
        StateRefresh = 21,      // host -> one client: full state of objects OUTSIDE their interest radius (low rate)

        // Voice channel (unreliable)
        VoiceFrame = 30,
    }

    public enum AuthorityKind : byte
    {
        /// <summary>Object owned/simulated by a specific peer (avatars, grabbed props).</summary>
        Peer = 0,
        /// <summary>Scene object owned by whoever is currently host (doors, shared radios). Migrates with the host.</summary>
        Scene = 1,
    }
}
