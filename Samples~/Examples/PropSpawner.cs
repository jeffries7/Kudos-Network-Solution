// =====================================================================
//  KNS EXAMPLE 6 — Spawning networked objects
// ---------------------------------------------------------------------
//  In the skeleton, Spawn()/Despawn() run on the host. Clients that
//  want something spawned ask via RpcHost — same request pattern as the
//  door example, and it keeps working after host migration.
//
//  Setup: scene object with KudosObject + this script. Assign a prefab
//  that is ALSO registered in KudosNetworkManager.NetworkedPrefabs
//  (spawning works by prefab hash — unregistered prefabs can't be
//  instantiated on remote peers).
//
//  Demonstrates:
//    • Runner.Spawn / Runner.Despawn (host-side)
//    • Client → host spawn requests via RpcHost
//    • Spawning WITH authority handed to the requester
// =====================================================================

using Kudos.Network;
using Kudos.Network.Rpc;
using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network.Examples
{
    public class PropSpawner : KudosBehaviour
    {
        [Tooltip("Must also be in KudosNetworkManager.NetworkedPrefabs.")]
        public KudosObject PropPrefab;

        [Tooltip("Props appear in a ring around this spawner.")]
        public float SpawnRadius = 1.5f;

        /// <summary>Call from a button / interaction. Works on host AND clients.</summary>
        public void SpawnOne()
        {
            // Route through the host; include who asked so authority can be
            // granted to them (they can then grab/move it immediately).
            RpcHost(nameof(HostSpawn), Runner.LocalPlayerId.Value);
        }

        [KudosRpc]
        public void HostSpawn(ushort requesterRaw)
        {
            if (!Runner.IsHost) return;   // belt and braces

            Vector2 ring = Random.insideUnitCircle.normalized * SpawnRadius;
            Vector3 pos  = transform.position + new Vector3(ring.x, 0.5f, ring.y);

            var obj = Runner.Spawn(PropPrefab, pos, Quaternion.identity,
                                   authority: new PlayerId(requesterRaw));

            Debug.Log($"[Spawner] Spawned {obj.name} ({obj.NetworkId.Value}) " +
                      $"owned by player {requesterRaw}");
        }

        /// <summary>Despawn everything this spawner's prefab produced (host only).</summary>
        public void DespawnAll()
        {
            if (!Runner.IsHost) { RpcHost(nameof(DespawnAll)); return; }

            // Iterate a copy — Despawn mutates the object list.
            var objects = new System.Collections.Generic.List<KudosObject>(Runner.Objects);
            foreach (var obj in objects)
                if (obj.PrefabHash == PropPrefab.PrefabHash)
                    Runner.Despawn(obj);
        }
    }
}
