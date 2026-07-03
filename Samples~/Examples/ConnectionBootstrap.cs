// =====================================================================
//  KNS EXAMPLE 1 — Connecting to a room
// ---------------------------------------------------------------------
//  Drop this on any GameObject in your startup scene (alongside a
//  configured KudosNetworkManager) to see the full connection lifecycle:
//  join-or-create, player join/leave events, and host migration.
//
//  Demonstrates:
//    • KudosNetworkManager.JoinOrCreateRoom(sceneKey, displayName)
//    • Subscribing to the five core lifecycle events
//    • Reading connection state (IsHost, LocalPlayerId, Peers)
// =====================================================================

using Kudos.Network;
using Kudos.Network.Rpc;
using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network.Examples
{
    public class ConnectionBootstrap : MonoBehaviour
    {
        [Tooltip("Rooms are grouped by scene key. Everyone asking for the same key fills the same rooms.")]
        public string SceneKey = "plaza";

        [Tooltip("Shown to other players (stored on your avatar's DisplayName SyncVar).")]
        public string DisplayName = "Player";

        [Tooltip("Connect automatically on Start. Turn off if you call Connect() from your own menu UI.")]
        public bool AutoConnect = true;

        private void Start()
        {
            var net = KudosNetworkManager.Instance;

            net.OnJoinedRoom            += HandleJoinedRoom;
            net.OnPlayerJoined          += peer => Debug.Log($"[KNS] {peer.DisplayName} joined (player {peer.PlayerId.Value})");
            net.OnPlayerLeft            += peer => Debug.Log($"[KNS] {peer.DisplayName} left");
            net.OnHostMigrated          += newHost => Debug.Log($"[KNS] Host migrated — new host is player {newHost.Value}");
            net.OnDisconnectedFromRoom  += reason => Debug.Log($"[KNS] Disconnected: {reason}");

            if (AutoConnect) Connect();
        }

        /// <summary>Call this from a UI button if AutoConnect is off.</summary>
        public void Connect()
        {
            Debug.Log($"[KNS] Requesting room for scene '{SceneKey}'…");
            KudosNetworkManager.Instance.JoinOrCreateRoom(SceneKey, DisplayName);
        }

        public void Disconnect()
        {
            KudosNetworkManager.Instance.LeaveRoom();
        }

        private void HandleJoinedRoom()
        {
            var net = KudosNetworkManager.Instance;
            Debug.Log($"[KNS] Joined room '{net.CurrentRoomId}' as player {net.LocalPlayerId.Value}. " +
                      $"IsHost={net.IsHost}, players in room: {net.Peers.Count}");
        }
    }
}
