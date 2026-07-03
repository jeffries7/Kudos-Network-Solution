// Kudos Network Solution (KNS)
// Host migration.
//
// THE P2P ACHILLES HEEL: in a listen-server room, the host closing their
// headset kills the party for 31 other people. Unacceptable for a social
// platform, so migration is a first-class KNS feature (Fusion host mode has a
// limited version; Mirror has none; dedicated-server products don't need it).
//
// WHY IT'S CHEAP IN KNS: distributed authority means EVERY peer already holds
// a complete, current copy of the world state - clients replicate everything,
// not just what they own. Migration therefore doesn't need a state download;
// it only needs (1) an election and (2) reconnection.
//
// PROTOCOL:
//   1. Every client detects host loss via transport disconnect (id 0).
//   2. Deterministic election, no messages needed: the surviving peer with the
//      LOWEST JoinOrder wins. Every client computes the same winner locally
//      from the roster it already has.
//   3. Winner: becomes host, claims the room in Nexus, waits for offers.
//   4. Losers: reconnect to the winner via signaling (peer ids are known from
//      the roster), with jittered retry.
//   5. Winner sends HostMigration(epoch) so everyone agrees on the new regime;
//      scene-authority objects re-home to the new host; departed host's avatar
//      is despawned.
//   6. Anyone who fails to reconnect within TimeoutSeconds falls back to
//      JoinOrCreateRoom(sceneKey) - they rejoin the same room via Nexus.

using System.Collections;
using Kudos.Network.Serialization;
using Kudos.Network.Transport;
using UnityEngine;

namespace Kudos.Network.Rooms
{
    public sealed class HostMigration
    {
        public const float ReconnectTimeoutSeconds = 10f;

        private readonly KudosNetworkManager _manager;
        private int _stateEpoch;   // increments per migration; stale-host packets are rejected
        private bool _migrating;

        public HostMigration(KudosNetworkManager manager) => _manager = manager;

        public void OnHostLost(DisconnectReason reason)
        {
            if (_migrating) return;
            _migrating = true;
            Debug.LogWarning($"[KNS] Host lost ({reason}) - starting migration, epoch {_stateEpoch + 1}");

            // 1) Remove the departed host from our local roster.
            var departedHost = _manager.HostPlayerId;

            // 2) Deterministic election: lowest surviving JoinOrder.
            KudosPeer winner = null;
            foreach (var peer in _manager.Peers.Values)
            {
                if (peer.PlayerId == departedHost) continue;
                if (winner == null || peer.JoinOrder < winner.JoinOrder) winner = peer;
            }
            if (winner == null) { Fallback("no surviving peers"); return; }

            _stateEpoch++;
            _manager.BecomeHostAfterMigration(winner.PlayerId);

            if (winner.IsLocal)
                _manager.StartCoroutine(BecomeHost());
            else
                _manager.StartCoroutine(ReconnectToNewHost(winner));
        }

        private IEnumerator BecomeHost()
        {
            Debug.Log("[KNS] Won host election - claiming room");
            // Continue using PlayerId range far above any assigned so far (skeleton: +1000 headroom).
            _manager.AllocatePlayerIdRangeAfterMigration((ushort)(1000 * _stateEpoch));

            _manager.Transport.StartHost(_manager.CurrentRoomId);
            _manager.Rooms.ClaimRoomAsNewHost();

            // Give stragglers a beat to reconnect, then announce the new epoch.
            yield return new WaitForSeconds(1f);
            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.HostMigration);
            w.WriteInt(_stateEpoch);
            w.WriteUShort(_manager.LocalPlayerId.Value);
            _manager.Transport.Broadcast(w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
            _migrating = false;
        }

        private IEnumerator ReconnectToNewHost(KudosPeer newHost)
        {
            // Jitter reconnects so 30 clients don't stampede the new host's signaling in the same frame.
            yield return new WaitForSeconds(Random.Range(0.1f, 1.0f));

            float deadline = Time.realtimeSinceStartup + ReconnectTimeoutSeconds;
            Debug.Log($"[KNS] Reconnecting to new host {newHost.DisplayName} ({newHost.SignalingPeerId})");
            _manager.Transport.Connect(_manager.CurrentRoomId, newHost.SignalingPeerId);

            while (Time.realtimeSinceStartup < deadline)
            {
                if (_manager.IsConnected) { _migrating = false; yield break; }
                yield return null;
            }
            Fallback("reconnect timed out");
        }

        public void HandleMigrationMessage(KudosReader reader)
        {
            int epoch = reader.ReadInt();
            var newHostId = new PlayerId(reader.ReadUShort());
            if (epoch <= _stateEpoch && !_migrating) return; // stale
            _stateEpoch = epoch;
            _manager.BecomeHostAfterMigration(newHostId);
            _migrating = false;
            Debug.Log($"[KNS] Migration complete - {newHostId} is host (epoch {epoch})");
        }

        private void Fallback(string why)
        {
            Debug.LogWarning($"[KNS] Migration fallback ({why}) - rejoining via Nexus");
            _migrating = false;
            string scene = _manager.CurrentSceneKey;
            _manager.LeaveRoom();
            _manager.JoinOrCreateRoom(scene);
        }
    }
}
