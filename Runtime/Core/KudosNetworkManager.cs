// Kudos Network Solution (KNS)
// KudosNetworkManager - the single entry point.
// (Fusion: NetworkRunner | Mirror: NetworkManager | Coherence: CoherenceBridge)
//
// One component in your bootstrap scene. Owns:
//   * the transport + signaling connection
//   * the fixed network tick (Fusion-style, decoupled from render rate)
//   * peer roster, object registry, spawn/despawn, authority arbitration
//   * message routing between subsystems (replication, rpc, voice, migration)

using System;
using System.Collections.Generic;
using Kudos.Network.Rooms;
using Kudos.Network.Rpc;
using Kudos.Network.Serialization;
using Kudos.Network.Simulation;
using Kudos.Network.Transport;
using UnityEngine;

namespace Kudos.Network
{
    [AddComponentMenu("Kudos/Kudos Network Manager")]
    public sealed class KudosNetworkManager : MonoBehaviour
    {
        public static KudosNetworkManager Instance { get; private set; }

        [Header("Nexus backend")]
        [Tooltip("Your self-hosted signaling + room registry, e.g. wss://nexus.kudos.example.com")]
        public string NexusUrl = "ws://localhost:8787";
        public RtcConfig RtcConfig = new RtcConfig();

        [Header("Simulation")]
        [Tooltip("Network ticks per second. 20 Hz is the sweet spot for slow-paced social VR: smooth with interpolation, and a 32-player room stays within Quest-friendly uplink.")]
        [Range(10, 60)] public int TickRate = 20;

        [Header("Rooms")]
        [Range(2, 32)] public int MaxPlayersPerRoom = 32;

        [Header("Interest management (host-side AOI)")]
        [Tooltip("Host only relays object deltas to peers whose avatar is within InterestRadius of the object; everything else refreshes at low rate. Cuts host upload 3-5x in typical social spaces.")]
        public bool EnableInterestManagement = true;
        [Tooltip("Full-rate replication radius, metres (measured avatar head to object).")]
        public float InterestRadius = 20f;
        [Tooltip("Objects OUTSIDE the radius get a full-state refresh every this many ticks (20 = 1 Hz at default tick rate). 0 disables the far tier (not recommended).")]
        public int FarRefreshIntervalTicks = 20;

        [Header("Reconnect")]
        [Tooltip("When a peer's link drops, the host freezes their presence and holds their state this long. If they reconnect in time they resume with the same PlayerId and their avatar never despawns. 0 = disabled.")]
        public float ReconnectGraceSeconds = 15f;

        [Header("Prefabs")]
        [Tooltip("Every networked prefab must be registered here (or via RegisterPrefab at runtime).")]
        public List<KudosObject> NetworkedPrefabs = new List<KudosObject>();

        [Tooltip("Spawned automatically for each player on join.")]
        public KudosObject PlayerAvatarPrefab;

        // ---- runtime state --------------------------------------------------
        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public PlayerId LocalPlayerId { get; private set; } = PlayerId.None;
        public PlayerId HostPlayerId { get; private set; } = PlayerId.None;
        public string CurrentRoomId { get; private set; }
        public string CurrentSceneKey { get; private set; }
        public float NetworkTime { get; private set; }
        public int CurrentTick { get; private set; }

        public ITransport Transport { get; private set; }
        public SignalingClient Signaling { get; private set; }
        public RpcSystem Rpc { get; private set; }
        public ReplicationSystem Replication { get; private set; }
        public RoomManager Rooms { get; private set; }
        public HostMigration Migration { get; private set; }
        public InterestManagement Interest { get; private set; }

        public IReadOnlyDictionary<PlayerId, KudosPeer> Peers => _peers;
        public IReadOnlyList<KudosObject> Objects => _objectList;

        // ---- events ----------------------------------------------------------
        public event Action<KudosPeer> OnPlayerJoined;
        public event Action<KudosPeer> OnPlayerLeft;
        public event Action OnJoinedRoom;
        public event Action<string> OnDisconnectedFromRoom;
        public event Action<PlayerId> OnHostMigrated;
        /// <summary>A peer's link dropped; the host is holding their state (avatar freezes rather than despawns).</summary>
        public event Action<KudosPeer> OnPlayerAway;
        /// <summary>An away peer reconnected within the grace window - same PlayerId, same avatar.</summary>
        public event Action<KudosPeer> OnPlayerResumed;
        /// <summary>WE were kicked from the room by the host (reason attached).</summary>
        public event Action<string> OnKicked;

        // ---- internals -------------------------------------------------------
        private readonly Dictionary<PlayerId, KudosPeer> _peers = new Dictionary<PlayerId, KudosPeer>();
        private readonly Dictionary<int, PlayerId> _connectionToPlayer = new Dictionary<int, PlayerId>();
        private readonly Dictionary<uint, KudosObject> _objects = new Dictionary<uint, KudosObject>();
        private readonly List<KudosObject> _objectList = new List<KudosObject>();
        private readonly Dictionary<uint, KudosObject> _prefabsByHash = new Dictionary<uint, KudosObject>();

        private uint _nextNetworkId = 1;     // host-assigned
        private ushort _nextPlayerId = 1;    // host-assigned (host itself is 0 on room creation)
        private int _nextJoinOrder;
        private float _tickAccumulator;

        // ---- reconnect grace / kick internals ---------------------------------
        private uint _myResumeToken;                 // client: handed out in Welcome, proves identity on resume
        private bool _resumePending;                 // client: mid resume attempt
        private DisconnectReason _lastDisconnectReason;
        private bool _wasKicked;                     // client: suppress resume/migration after a kick
        private readonly HashSet<string> _kickedSignalingIds = new HashSet<string>(); // host: session deny-list
        private readonly List<PlayerId> _awaySweepScratch = new List<PlayerId>(4);
        private static readonly System.Random TokenRng = new System.Random();

        // ====================================================================== lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            foreach (var prefab in NetworkedPrefabs) RegisterPrefab(prefab);
            if (PlayerAvatarPrefab != null) RegisterPrefab(PlayerAvatarPrefab);
        }

        public void RegisterPrefab(KudosObject prefab)
        {
            if (prefab == null) return;
            if (prefab.PrefabHash == 0)
                prefab.PrefabHash = RpcSystem.Fnv1a(prefab.name); // editor tooling normally bakes this
            _prefabsByHash[prefab.PrefabHash] = prefab;
        }

        /// <summary>
        /// THE entry point: join a room hosting `sceneKey`, or become the host of a
        /// fresh one if every existing room is full. Fill-or-create is decided by
        /// the Nexus backend atomically, so two simultaneous joiners can't race.
        /// </summary>
        public void JoinOrCreateRoom(string sceneKey, string displayName = "Player")
        {
            CurrentSceneKey = sceneKey;
            _wasKicked = false;
            _resumePending = false;

            Signaling = new SignalingClient();
            Transport = new WebRtcTransport(Signaling, RtcConfig);
            Rpc = new RpcSystem(this);
            Replication = new ReplicationSystem(this);
            Interest = new InterestManagement(this);
            Migration = new HostMigration(this);
            Rooms = new RoomManager(this, Signaling);

            Transport.OnPeerConnected += HandlePeerConnected;
            Transport.OnData += HandleData;
            Transport.OnPeerDisconnected += HandlePeerDisconnected;

            Rooms.OnRoomAssigned += assignment =>
            {
                CurrentRoomId = assignment.roomId;
                if (assignment.isHost) StartAsHost(displayName);
                else StartAsClient(assignment.hostPeerId, displayName);
            };

            Signaling.OnConnected += () => Rooms.RequestJoinOrCreate(sceneKey, MaxPlayersPerRoom);
            Signaling.OnClosed += reason => { if (!IsConnected) OnDisconnectedFromRoom?.Invoke($"Nexus unreachable: {reason}"); };
            Signaling.Connect(NexusUrl);
        }

        public void LeaveRoom()
        {
            Rooms?.NotifyLeaving();
            Transport?.Shutdown();
            Signaling?.Dispose();
            ResetState("left room");
        }

        private void StartAsHost(string displayName)
        {
            IsHost = true;
            IsConnected = true;
            LocalPlayerId = new PlayerId(0);
            HostPlayerId = LocalPlayerId;
            AddPeer(new KudosPeer
            {
                PlayerId = LocalPlayerId, IsLocal = true, IsHost = true,
                DisplayName = displayName, JoinOrder = _nextJoinOrder++,
                SignalingPeerId = Signaling.PeerId
            });
            Transport.StartHost(CurrentRoomId);
            SpawnAvatarFor(LocalPlayerId);
            OnJoinedRoom?.Invoke();
            Debug.Log($"[KNS] Hosting room {CurrentRoomId} (scene '{CurrentSceneKey}')");
        }

        private void StartAsClient(string hostPeerId, string displayName)
        {
            IsHost = false;
            _pendingDisplayName = displayName;
            Transport.Connect(CurrentRoomId, hostPeerId);
            Debug.Log($"[KNS] Joining room {CurrentRoomId} via host {hostPeerId}");
        }
        private string _pendingDisplayName;

        // ====================================================================== tick loop

        private void Update()
        {
            Transport?.PollEvents();
            if (!IsConnected) { Signaling?.PollEvents(); return; }

            float tickInterval = 1f / TickRate;
            _tickAccumulator += Time.deltaTime;
            while (_tickAccumulator >= tickInterval)
            {
                _tickAccumulator -= tickInterval;
                NetworkTime += tickInterval;
                CurrentTick++;

                for (int i = 0; i < _objectList.Count; i++)
                    foreach (var behaviour in _objectList[i].Behaviours)
                        behaviour.NetworkFixedUpdate(tickInterval);

                Replication.SendDeltas();
                Interest?.HostTick(CurrentTick);
            }

            if (IsHost) SweepAwayPeers();
        }

        /// <summary>Evict away peers whose reconnect grace window expired.</summary>
        private void SweepAwayPeers()
        {
            _awaySweepScratch.Clear();
            foreach (var peer in _peers.Values)
                if (peer.IsAway && Time.realtimeSinceStartup > peer.AwayDeadline)
                    _awaySweepScratch.Add(peer.PlayerId);

            foreach (var playerId in _awaySweepScratch)
            {
                Debug.Log($"[KNS] {playerId} did not reconnect in time - removing");
                RemovePeerAsHost(playerId);
            }
        }

        // ====================================================================== connection events

        private void HandlePeerConnected(int connectionId)
        {
            if (!IsHost)
            {
                // Connected to the host. It will send Welcome; we introduce ourselves first.
                // Resume: prove we're the same player so the host restores rather than re-adds.
                var writer = KudosWriter.Rent();
                writer.WriteByte((byte)MsgId.Ping);
                writer.WriteString(_pendingDisplayName ?? "Player");
                writer.WriteString(Signaling.PeerId);
                writer.WriteBool(_resumePending);
                writer.WriteUShort(_resumePending ? LocalPlayerId.Value : PlayerId.None.Value);
                writer.WriteUInt(_resumePending ? _myResumeToken : 0u);
                Transport.Send(0, writer.ToSegment(), KudosChannel.Reliable);
                KudosWriter.Return(writer);
                return;
            }
            // Host: wait for the joiner's Ping (name + signaling id) before Welcome.
            Debug.Log($"[KNS] Connection {connectionId} opened, awaiting introduction");
        }

        private void HandlePeerDisconnected(int connectionId, DisconnectReason reason)
        {
            if (IsHost)
            {
                if (!_connectionToPlayer.TryGetValue(connectionId, out var playerId)) return;
                _connectionToPlayer.Remove(connectionId);

                // Reconnect grace: freeze their presence instead of removing.
                if (ReconnectGraceSeconds > 0f && _peers.TryGetValue(playerId, out var peer))
                {
                    MarkPeerAway(peer);
                    return;
                }
                RemovePeerAsHost(playerId);
            }
            else if (connectionId == 0)
            {
                if (_wasKicked) return; // no resume, no migration - we were removed on purpose

                // Lost the host. This is ambiguous from here: OUR link may have blipped
                // (host fine, resume) or the HOST may have died (migrate). Try the cheap
                // one first - Nexus answers rejoin-room instantly either way.
                if (!_resumePending && LocalPlayerId.IsValid && ReconnectGraceSeconds > 0f)
                {
                    _resumePending = true;
                    _lastDisconnectReason = reason;
                    Debug.Log("[KNS] Lost host - attempting to resume session...");
                    RebuildTransportForResume();
                }
                else
                {
                    _resumePending = false;
                    Migration.OnHostLost(reason);
                }
            }
        }

        /// <summary>Host: peer link dropped - hold their state for the grace window.</summary>
        private void MarkPeerAway(KudosPeer peer)
        {
            peer.ConnectionId = -1;
            peer.IsAway = true;
            peer.AwayDeadline = Time.realtimeSinceStartup + ReconnectGraceSeconds;

            // Scene objects they held return to the host so the world stays usable;
            // their Peer-kind objects (avatar) freeze in place, still visible.
            foreach (var obj in _objectList)
                if (obj.Authority == peer.PlayerId && obj.AuthorityKind == AuthorityKind.Scene)
                    GrantAuthority(obj, HostPlayerId);

            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.PeerAway);
            w.WriteUShort(peer.PlayerId.Value);
            Transport.Broadcast(w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);

            OnPlayerAway?.Invoke(peer);
            Debug.Log($"[KNS] {peer.DisplayName} went away - holding state for {ReconnectGraceSeconds:0}s");
        }

        /// <summary>
        /// Client: tear down the dead transport and reconnect to the SAME room via
        /// signaling. On success we re-introduce with our resume token; the host
        /// restores our identity. If the room is gone (host actually died), the
        /// room-error path hands over to host migration.
        /// </summary>
        private void RebuildTransportForResume()
        {
            Transport?.Shutdown();
            Signaling?.Dispose();

            Signaling = new SignalingClient();
            Transport = new WebRtcTransport(Signaling, RtcConfig);
            Transport.OnPeerConnected += HandlePeerConnected;
            Transport.OnData += HandleData;
            Transport.OnPeerDisconnected += HandlePeerDisconnected;

            Rooms = new RoomManager(this, Signaling);
            Rooms.OnRoomAssigned += assignment => Transport.Connect(CurrentRoomId, assignment.hostPeerId);
            Rooms.OnRoomError += _ =>
            {
                Debug.Log("[KNS] Room gone - host died. Falling back to migration.");
                _resumePending = false;
                Migration.OnHostLost(_lastDisconnectReason);
            };

            Signaling.OnConnected += () => Rooms.RequestRejoin(CurrentRoomId);
            Signaling.OnClosed += reason =>
            {
                if (_resumePending)
                {
                    _resumePending = false;
                    Migration.OnHostLost(_lastDisconnectReason);
                }
            };
            Signaling.Connect(NexusUrl);
        }

        // ====================================================================== message routing

        private void HandleData(int connectionId, ArraySegment<byte> payload, KudosChannel channel)
        {
            var reader = new KudosReader(payload);
            var msgId = (MsgId)reader.ReadByte();
            switch (msgId)
            {
                case MsgId.Ping when IsHost: HandleIntroduction(connectionId, reader); break;
                case MsgId.Welcome: HandleWelcome(reader); break;
                case MsgId.PeerJoined: HandlePeerJoinedMsg(reader); break;
                case MsgId.PeerLeft: HandlePeerLeftMsg(reader); break;
                case MsgId.Spawn: HandleSpawnMsg(reader, payload, connectionId); break;
                case MsgId.Despawn: HandleDespawnMsg(reader, payload, connectionId); break;
                case MsgId.Rpc: HandleRpcMsg(reader, payload, connectionId); break;
                case MsgId.StateDelta: Replication.HandleStateDelta(reader, payload, connectionId); break;
                case MsgId.FullStateSync: Replication.ReadFullState(reader); break;
                case MsgId.AuthorityRequest when IsHost: HandleAuthorityRequest(connectionId, reader); break;
                case MsgId.AuthorityGranted: HandleAuthorityGranted(reader, payload, connectionId); break;
                case MsgId.VoiceFrame: Voip.KudosVoice.Instance?.HandleVoicePacket(reader, payload, connectionId); break;
                case MsgId.HostMigration: Migration.HandleMigrationMessage(reader); break;
                case MsgId.Kick: HandleKickMsg(reader); break;
                case MsgId.PeerAway: HandlePeerAwayMsg(reader); break;
                case MsgId.PeerResumed: HandlePeerResumedMsg(reader); break;
                case MsgId.StateRefresh: Interest?.HandleStateRefresh(reader); break;
            }
        }

        // ---------------------------------------------------------------- join flow (host side)

        private void HandleIntroduction(int connectionId, KudosReader reader)
        {
            string displayName = reader.ReadString();
            string signalingId = reader.ReadString();
            bool wantsResume = reader.ReadBool();
            var resumeId = new PlayerId(reader.ReadUShort());
            uint resumeToken = reader.ReadUInt();

            // Kicked this session? Refuse - tell them why, then drop the link.
            if (_kickedSignalingIds.Contains(signalingId))
            {
                var kw = KudosWriter.Rent();
                kw.WriteByte((byte)MsgId.Kick);
                kw.WriteString("You were removed from this room.");
                Transport.Send(connectionId, kw.ToSegment(), KudosChannel.Reliable);
                KudosWriter.Return(kw);
                Transport.Disconnect(connectionId);
                return;
            }

            // Resume path: same PlayerId, avatar never despawned, world state re-synced.
            if (wantsResume &&
                _peers.TryGetValue(resumeId, out var awayPeer) &&
                awayPeer.IsAway && awayPeer.ResumeToken == resumeToken)
            {
                ResumePeer(awayPeer, connectionId, signalingId);
                return;
            }

            // Fresh join (also the fallback when a resume token expired).
            var playerId = new PlayerId(_nextPlayerId++);
            var peer = new KudosPeer
            {
                PlayerId = playerId, ConnectionId = connectionId, DisplayName = displayName,
                SignalingPeerId = signalingId, JoinOrder = _nextJoinOrder++,
                ResumeToken = NewResumeToken()
            };
            AddPeer(peer);
            _connectionToPlayer[connectionId] = playerId;

            SendWelcomeAndState(peer);

            // Tell everyone else
            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.PeerJoined);
            w.WriteUShort(playerId.Value);
            w.WriteString(displayName);
            w.WriteString(signalingId);
            w.WriteInt(peer.JoinOrder);
            Transport.Broadcast(w.ToSegment(), KudosChannel.Reliable, exceptConnectionId: connectionId);
            KudosWriter.Return(w);

            // Spawn their avatar (host spawns on behalf, authority = them)
            SpawnAvatarFor(playerId);
            Rooms.ReportPlayerCount(_peers.Count);
        }

        /// <summary>Welcome (id + roster + resume token) followed by full world state.</summary>
        private void SendWelcomeAndState(KudosPeer peer)
        {
            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.Welcome);
            w.WriteUShort(peer.PlayerId.Value);
            w.WriteUShort(HostPlayerId.Value);
            w.WriteUInt(peer.ResumeToken);
            w.WriteVarUInt((uint)_peers.Count);
            foreach (var p in _peers.Values)
            {
                w.WriteUShort(p.PlayerId.Value);
                w.WriteString(p.DisplayName);
                w.WriteString(p.SignalingPeerId);
                w.WriteInt(p.JoinOrder);
                w.WriteBool(p.IsAway);
            }
            Transport.Send(peer.ConnectionId, w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);

            w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.FullStateSync);
            Replication.WriteFullState(w);
            Transport.Send(peer.ConnectionId, w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
        }

        /// <summary>Host: restore an away peer on the new connection - same identity, no respawn.</summary>
        private void ResumePeer(KudosPeer peer, int connectionId, string newSignalingId)
        {
            peer.ConnectionId = connectionId;
            peer.SignalingPeerId = newSignalingId; // signaling identity changes across reconnects
            peer.IsAway = false;
            peer.ResumeToken = NewResumeToken();   // rotate - tokens are single-use
            _connectionToPlayer[connectionId] = peer.PlayerId;

            SendWelcomeAndState(peer);             // re-sync everything they missed

            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.PeerResumed);
            w.WriteUShort(peer.PlayerId.Value);
            w.WriteString(newSignalingId);
            Transport.Broadcast(w.ToSegment(), KudosChannel.Reliable, exceptConnectionId: connectionId);
            KudosWriter.Return(w);

            OnPlayerResumed?.Invoke(peer);
            Debug.Log($"[KNS] {peer.DisplayName} resumed as {peer.PlayerId}");
        }

        private static uint NewResumeToken()
            => (uint)TokenRng.Next(1, int.MaxValue) ^ ((uint)TokenRng.Next() << 16);

        // ---------------------------------------------------------------- join flow (client side)

        private void HandleWelcome(KudosReader reader)
        {
            bool isResume = _resumePending;
            _resumePending = false;

            LocalPlayerId = new PlayerId(reader.ReadUShort());
            HostPlayerId = new PlayerId(reader.ReadUShort());
            _myResumeToken = reader.ReadUInt();

            if (isResume) _peers.Clear(); // roster may have changed while we were gone

            uint peerCount = reader.ReadVarUInt();
            for (uint i = 0; i < peerCount; i++)
            {
                var peer = new KudosPeer
                {
                    PlayerId = new PlayerId(reader.ReadUShort()),
                    DisplayName = reader.ReadString(),
                    SignalingPeerId = reader.ReadString(),
                    JoinOrder = reader.ReadInt(),
                    IsAway = reader.ReadBool()
                };
                peer.IsLocal = peer.PlayerId == LocalPlayerId;
                peer.IsHost = peer.PlayerId == HostPlayerId;
                if (peer.IsHost) peer.ConnectionId = 0;
                AddPeer(peer, quiet: isResume); // resuming: don't re-fire join events for the whole room
            }
            IsConnected = true;

            if (isResume)
            {
                Debug.Log($"[KNS] Resumed session as {LocalPlayerId}");
                if (_peers.TryGetValue(LocalPlayerId, out var me)) OnPlayerResumed?.Invoke(me);
            }
            else
            {
                OnJoinedRoom?.Invoke();
                Debug.Log($"[KNS] Joined as {LocalPlayerId}, {peerCount} peers in room");
            }
        }

        private void HandlePeerJoinedMsg(KudosReader reader)
        {
            var peer = new KudosPeer
            {
                PlayerId = new PlayerId(reader.ReadUShort()),
                DisplayName = reader.ReadString(),
                SignalingPeerId = reader.ReadString(),
                JoinOrder = reader.ReadInt()
            };
            AddPeer(peer);
        }

        private void HandlePeerLeftMsg(KudosReader reader)
        {
            var playerId = new PlayerId(reader.ReadUShort());
            RemovePeerLocal(playerId);
        }

        private void HandlePeerAwayMsg(KudosReader reader)
        {
            var playerId = new PlayerId(reader.ReadUShort());
            if (_peers.TryGetValue(playerId, out var peer))
            {
                peer.IsAway = true;
                OnPlayerAway?.Invoke(peer);
            }
        }

        private void HandlePeerResumedMsg(KudosReader reader)
        {
            var playerId = new PlayerId(reader.ReadUShort());
            string newSignalingId = reader.ReadString();
            if (_peers.TryGetValue(playerId, out var peer))
            {
                peer.IsAway = false;
                peer.SignalingPeerId = newSignalingId;
                OnPlayerResumed?.Invoke(peer);
            }
        }

        private void HandleKickMsg(KudosReader reader)
        {
            string reason = reader.ReadString();
            _wasKicked = true;
            Debug.Log($"[KNS] Kicked from room: {reason}");
            OnKicked?.Invoke(reason);
            Rooms?.NotifyLeaving();
            Transport?.Shutdown();
            Signaling?.Dispose();
            ResetState($"kicked: {reason}");
        }

        // ---------------------------------------------------------------- kick (host)

        /// <summary>
        /// Host only: remove a player from the room. They receive the reason, are
        /// disconnected, and this host refuses their signaling identity for the rest
        /// of the session. TODO(integration): persist bans at the Nexus/account level.
        /// </summary>
        public void KickPlayer(PlayerId playerId, string reason = "Removed by host")
        {
            if (!IsHost) { Debug.LogError("[KNS] Only the host can kick"); return; }
            if (playerId == LocalPlayerId) return;
            if (!_peers.TryGetValue(playerId, out var peer)) return;

            _kickedSignalingIds.Add(peer.SignalingPeerId);

            if (peer.ConnectionId >= 0)
            {
                var w = KudosWriter.Rent();
                w.WriteByte((byte)MsgId.Kick);
                w.WriteString(reason);
                Transport.Send(peer.ConnectionId, w.ToSegment(), KudosChannel.Reliable);
                KudosWriter.Return(w);
                // NOTE: the disconnect may race the message on some transports; the
                // deny-list makes it self-healing (their resume attempt is refused
                // with the same Kick message, delivered reliably).
                Transport.Disconnect(peer.ConnectionId);
            }
            RemovePeerAsHost(playerId);
            Debug.Log($"[KNS] Kicked {peer.DisplayName}: {reason}");
        }

        // ---------------------------------------------------------------- peers

        private void AddPeer(KudosPeer peer, bool quiet = false)
        {
            bool isNew = !_peers.ContainsKey(peer.PlayerId);
            _peers[peer.PlayerId] = peer;
            if (isNew && !quiet) OnPlayerJoined?.Invoke(peer);
        }

        private void RemovePeerAsHost(PlayerId playerId)
        {
            RemovePeerLocal(playerId);

            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.PeerLeft);
            w.WriteUShort(playerId.Value);
            Transport.Broadcast(w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
            Rooms.ReportPlayerCount(_peers.Count);
        }

        private void RemovePeerLocal(PlayerId playerId)
        {
            if (!_peers.TryGetValue(playerId, out var peer)) return;
            _peers.Remove(playerId);
            if (peer.ConnectionId >= 0) _connectionToPlayer.Remove(peer.ConnectionId);
            Interest?.OnPeerRemoved(playerId);

            // Despawn their objects; scene objects they held fall back to the host.
            var toDespawn = new List<KudosObject>();
            foreach (var obj in _objectList)
            {
                if (obj.Authority != playerId) continue;
                if (obj.AuthorityKind == AuthorityKind.Peer) toDespawn.Add(obj);
                else obj.SetAuthority(HostPlayerId);
            }
            foreach (var obj in toDespawn) DespawnLocal(obj);

            OnPlayerLeft?.Invoke(peer);
        }

        // ====================================================================== spawn / despawn

        public KudosObject Spawn(KudosObject prefab, Vector3 position, Quaternion rotation, PlayerId authority)
        {
            if (!IsConnected) { Debug.LogError("[KNS] Spawn before connected"); return null; }
            if (!IsHost)
            {
                // Skeleton simplification: clients request spawns via an RPC-style path in
                // production; here spawning is host-driven (avatars are host-spawned already).
                Debug.LogError("[KNS] Client-initiated Spawn not implemented in skeleton - route via a host RPC.");
                return null;
            }

            var netId = new NetworkId(_nextNetworkId++);
            var obj = InstantiateBound(prefab, position, rotation, netId, authority, prefab.AuthorityKind);

            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.Spawn);
            w.WriteVarUInt(netId.Value);
            w.WriteUInt(prefab.PrefabHash);
            w.WriteUShort(authority.Value);
            w.WriteByte((byte)prefab.AuthorityKind);
            w.WriteVector3Quantized(position);
            w.WriteQuaternionQuantized(rotation);
            obj.SerializeFull(w);
            Transport.Broadcast(w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
            return obj;
        }

        public void Despawn(KudosObject obj)
        {
            if (!IsHost) { Debug.LogError("[KNS] Only the host despawns in the skeleton - route via RpcHost."); return; }
            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.Despawn);
            w.WriteVarUInt(obj.NetworkId.Value);
            Transport.Broadcast(w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
            DespawnLocal(obj);
        }

        private void SpawnAvatarFor(PlayerId playerId)
        {
            if (PlayerAvatarPrefab == null) return;
            if (IsHost) Spawn(PlayerAvatarPrefab, Vector3.zero, Quaternion.identity, playerId);
        }

        private void HandleSpawnMsg(KudosReader reader, ArraySegment<byte> raw, int fromConnection)
        {
            var netId = new NetworkId(reader.ReadVarUInt());
            uint prefabHash = reader.ReadUInt();
            var authority = new PlayerId(reader.ReadUShort());
            var kind = (AuthorityKind)reader.ReadByte();
            var pos = reader.ReadVector3Quantized();
            var rot = reader.ReadQuaternionQuantized();

            if (_objects.ContainsKey(netId.Value)) return; // duplicate (migration replays)
            var obj = InstantiateFromPrefabHash(prefabHash, netId, authority, kind, pos, rot);
            obj?.DeserializeFull(reader);
        }

        private void HandleDespawnMsg(KudosReader reader, ArraySegment<byte> raw, int fromConnection)
        {
            var netId = new NetworkId(reader.ReadVarUInt());
            if (TryGetObject(netId, out var obj)) DespawnLocal(obj);
        }

        internal KudosObject InstantiateFromPrefabHash(uint prefabHash, NetworkId netId, PlayerId authority,
            AuthorityKind kind, Vector3 pos = default, Quaternion rot = default)
        {
            if (!_prefabsByHash.TryGetValue(prefabHash, out var prefab))
            {
                Debug.LogError($"[KNS] Unknown prefab hash {prefabHash} - register the prefab on every build.");
                return null;
            }
            if (rot == default) rot = Quaternion.identity;
            return InstantiateBound(prefab, pos, rot, netId, authority, kind);
        }

        private KudosObject InstantiateBound(KudosObject prefab, Vector3 pos, Quaternion rot,
            NetworkId netId, PlayerId authority, AuthorityKind kind)
        {
            var obj = Instantiate(prefab, pos, rot);
            obj.Bind();
            obj.NetworkId = netId;
            obj.AuthorityKind = kind;
            _objects[netId.Value] = obj;
            _objectList.Add(obj);
            obj.SetAuthority(authority);
            obj.InvokeSpawned();
            return obj;
        }

        private void DespawnLocal(KudosObject obj)
        {
            obj.InvokeDespawned();
            Interest?.OnObjectDespawned(obj.NetworkId);
            _objects.Remove(obj.NetworkId.Value);
            _objectList.Remove(obj);
            Destroy(obj.gameObject);
        }

        public bool TryGetObject(NetworkId id, out KudosObject obj) => _objects.TryGetValue(id.Value, out obj);

        // ====================================================================== authority arbitration (host)

        internal void RequestAuthority(KudosObject obj)
        {
            if (obj.HasAuthority) return;
            if (IsHost) { GrantAuthority(obj, LocalPlayerId); return; }

            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.AuthorityRequest);
            w.WriteVarUInt(obj.NetworkId.Value);
            Transport.Send(0, w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
        }

        internal void ReleaseAuthority(KudosObject obj)
        {
            if (!obj.HasAuthority) return;
            if (IsHost) { GrantAuthority(obj, HostPlayerId); return; }
            // Client releasing = ask host to take it back.
            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.AuthorityRequest);
            w.WriteVarUInt(obj.NetworkId.Value);
            // requester wanting HOST authority is encoded by requesting an object we own
            Transport.Send(0, w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
        }

        private void HandleAuthorityRequest(int fromConnection, KudosReader reader)
        {
            var netId = new NetworkId(reader.ReadVarUInt());
            if (!TryGetObject(netId, out var obj)) return;
            if (!_connectionToPlayer.TryGetValue(fromConnection, out var requester)) return;

            // Arbitration: current holder requesting = release back to host.
            var newAuthority = obj.Authority == requester ? HostPlayerId : requester;

            // First-come-first-served: a second grab request that arrives while
            // someone else already holds authority is DENIED unless it's a release.
            if (obj.Authority != HostPlayerId && obj.Authority != requester && obj.AuthorityKind == AuthorityKind.Peer)
            {
                // Peer-kind objects (avatars) are never transferable.
                return;
            }

            GrantAuthority(obj, newAuthority);
        }

        private void GrantAuthority(KudosObject obj, PlayerId newAuthority)
        {
            obj.SetAuthority(newAuthority);
            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.AuthorityGranted);
            w.WriteVarUInt(obj.NetworkId.Value);
            w.WriteUShort(newAuthority.Value);
            Transport.Broadcast(w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
        }

        private void HandleAuthorityGranted(KudosReader reader, ArraySegment<byte> raw, int fromConnection)
        {
            var netId = new NetworkId(reader.ReadVarUInt());
            var newAuthority = new PlayerId(reader.ReadUShort());
            if (TryGetObject(netId, out var obj)) obj.SetAuthority(newAuthority);
        }

        // ====================================================================== routing helpers

        internal void SendToHostOrBroadcast(ArraySegment<byte> payload, KudosChannel channel)
        {
            if (IsHost) Transport.Broadcast(payload, channel);
            else Transport.Send(0, payload, channel);
        }

        internal void RouteRpc(ArraySegment<byte> packet, RpcTarget target, PlayerId targetPlayer, bool invokeLocally)
        {
            if (IsHost)
            {
                switch (target)
                {
                    case RpcTarget.All:
                    case RpcTarget.Others:
                        Transport.Broadcast(packet, KudosChannel.Reliable);
                        break;
                    case RpcTarget.One:
                        if (_peers.TryGetValue(targetPlayer, out var peer) && peer.ConnectionId >= 0)
                            Transport.Send(peer.ConnectionId, packet, KudosChannel.Reliable);
                        else if (targetPlayer == LocalPlayerId) invokeLocally = true;
                        break;
                    case RpcTarget.Host:
                        invokeLocally = true;
                        break;
                }
            }
            else
            {
                Transport.Send(0, packet, KudosChannel.Reliable); // host relays per target byte
            }

            if (invokeLocally) Rpc.InvokeLocal(packet);
        }

        private void HandleRpcMsg(KudosReader reader, ArraySegment<byte> raw, int fromConnection)
        {
            if (IsHost)
            {
                // Peek target to relay. Packet layout: [msgId][netId var][bIdx][hash u32][target][player u16]...
                // Re-read via a scratch reader so the main reader position is untouched.
                var peek = new KudosReader(raw);
                peek.ReadByte();
                peek.ReadVarUInt();
                peek.ReadByte();
                peek.ReadUInt();
                var target = (RpcTarget)peek.ReadByte();
                var targetPlayer = new PlayerId(peek.ReadUShort());

                switch (target)
                {
                    case RpcTarget.All:
                        Transport.Broadcast(raw, KudosChannel.Reliable, exceptConnectionId: fromConnection);
                        break;
                    case RpcTarget.Others:
                        Transport.Broadcast(raw, KudosChannel.Reliable, exceptConnectionId: fromConnection);
                        break;
                    case RpcTarget.One:
                        if (_peers.TryGetValue(targetPlayer, out var p) && p.ConnectionId >= 0)
                        {
                            Transport.Send(p.ConnectionId, raw, KudosChannel.Reliable);
                            return; // not for the host
                        }
                        break;
                    case RpcTarget.Host:
                        break; // for us only
                }
                if (target == RpcTarget.One && targetPlayer != LocalPlayerId) return;
                Rpc.HandleIncoming(reader);
            }
            else
            {
                Rpc.HandleIncoming(reader);
            }
        }

        // ====================================================================== migration plumbing

        internal void BecomeHostAfterMigration(PlayerId newHostId)
        {
            IsHost = newHostId == LocalPlayerId;
            HostPlayerId = newHostId;
            foreach (var p in _peers.Values) p.IsHost = p.PlayerId == newHostId;
            OnHostMigrated?.Invoke(newHostId);
        }

        internal ushort AllocatePlayerIdRangeAfterMigration(ushort startFrom) => _nextPlayerId = startFrom;

        private void ResetState(string reason)
        {
            foreach (var obj in _objectList.ToArray()) DespawnLocal(obj);
            _peers.Clear();
            _connectionToPlayer.Clear();
            _objects.Clear();
            _objectList.Clear();
            Interest?.Clear();
            Utils.NetworkStats.Reset();
            _resumePending = false;
            IsConnected = false;
            IsHost = false;
            LocalPlayerId = PlayerId.None;
            OnDisconnectedFromRoom?.Invoke(reason);
        }

        private void OnDestroy()
        {
            if (Instance == this) LeaveRoom();
        }
    }
}
