// Kudos Network Solution (KNS)
// Room management - the fill-or-create flow.
//
// DESIGN LINEAGE (Coherence rooms + Photon's JoinRandomOrCreateRoom): the
// client expresses INTENT ("put me in a room hosting sceneKey X"); the Nexus
// backend makes the decision ATOMICALLY:
//
//     room with sceneKey == X and players < max exists?
//         yes -> assignment { isHost:false, hostPeerId } (join it)
//         no  -> assignment { isHost:true }              (you now host a new one)
//
// Doing this server-side (single-threaded Node event loop = free atomicity)
// eliminates the classic P2P race where two clients list rooms simultaneously,
// both see "no space" and both create rooms that each stay half full.

using System;
using Kudos.Network.Transport;
using UnityEngine;

namespace Kudos.Network.Rooms
{
    [Serializable]
    public struct RoomAssignment
    {
        public string roomId;
        public string sceneKey;
        public bool isHost;
        public string hostPeerId;   // valid when isHost == false
    }

    public sealed class RoomManager
    {
        private readonly KudosNetworkManager _manager;
        private readonly SignalingClient _signaling;
        private float _lastHeartbeat;

        public event Action<RoomAssignment> OnRoomAssigned;
        public event Action<string> OnRoomError;

        public RoomManager(KudosNetworkManager manager, SignalingClient signaling)
        {
            _manager = manager;
            _signaling = signaling;
            _signaling.OnMessage += HandleSignal;
        }

        public void RequestJoinOrCreate(string sceneKey, int maxPlayers)
        {
            _signaling.Send(new SignalMessage
            {
                type = "join-or-create",
                sceneKey = sceneKey,
                maxPlayers = maxPlayers
            });
        }

        /// <summary>
        /// Reconnect-grace path: rejoin a SPECIFIC room (not fill-or-create). Nexus
        /// answers room-assigned if the room and its host are alive, else an error -
        /// which is exactly the "was it my link or did the host die?" oracle.
        /// </summary>
        public void RequestRejoin(string roomId)
        {
            _signaling.Send(new SignalMessage
            {
                type = "rejoin-room",
                roomId = roomId
            });
        }

        /// <summary>Host keeps the registry's occupancy accurate so fill-or-create routes correctly.</summary>
        public void ReportPlayerCount(int count)
        {
            if (!_manager.IsHost) return;
            _signaling.Send(new SignalMessage
            {
                type = "heartbeat",
                roomId = _manager.CurrentRoomId,
                maxPlayers = count // reuse field as current count; backend reads by type
            });
        }

        public void NotifyLeaving()
        {
            if (_signaling == null || !_signaling.IsConnected) return;
            _signaling.Send(new SignalMessage { type = "leave", roomId = _manager.CurrentRoomId });
        }

        /// <summary>After winning a host-migration election, claim the room in the registry.</summary>
        public void ClaimRoomAsNewHost()
        {
            _signaling.Send(new SignalMessage
            {
                type = "claim-host",
                roomId = _manager.CurrentRoomId,
                sceneKey = _manager.CurrentSceneKey
            });
        }

        private void HandleSignal(SignalMessage msg)
        {
            switch (msg.type)
            {
                case "room-assigned":
                    OnRoomAssigned?.Invoke(new RoomAssignment
                    {
                        roomId = msg.roomId,
                        sceneKey = msg.sceneKey,
                        isHost = msg.isHost,
                        hostPeerId = msg.hostPeerId
                    });
                    break;
                case "error":
                    Debug.LogError($"[KNS] Nexus error: {msg.payload}");
                    OnRoomError?.Invoke(msg.payload);
                    break;
            }
        }
    }
}
