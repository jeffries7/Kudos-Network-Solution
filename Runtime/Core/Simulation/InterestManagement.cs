// Kudos Network Solution (KNS)
// InterestManagement - host-side area-of-interest (AOI) filtering.
//
// WHY: the host is the bandwidth bottleneck of a star topology. Without AOI
// it relays every object's deltas to every peer: O(objects x peers) upload.
// In a social space most objects are far from most people, so radius-based
// filtering cuts host upload by 3-5x in practice - the difference between
// "32 players on a Quest is borderline" and "comfortable".
//
// DESIGN (delta streams make this subtle - read carefully):
// Deltas carry ONLY dirty fields, and dirty flags clear on send. If the host
// simply dropped deltas for far objects, a peer would miss those changes
// FOREVER - eventual consistency would be broken. So AOI here is two-tier:
//
//   NEAR tier: objects within InterestRadius of the recipient's avatar head
//              receive full-rate deltas (relayed/filtered per recipient).
//   FAR tier:  everything else is refreshed with FULL object state at a low
//              rate (MsgId.StateRefresh every FarRefreshIntervalTicks) - the
//              host can do this because it applies every delta itself and
//              therefore always holds current values.
//
// A per-recipient near-set tracks tier transitions: the moment an object
// enters a recipient's radius they get an immediate full-state catch-up, so
// walking toward a whiteboard never shows a stale drawing.
//
// Voice uses the same idea independently (KudosVoice.MaxHearingDistance).

using System.Collections.Generic;
using Kudos.Network.Serialization;
using Kudos.Network.Transport;
using UnityEngine;

namespace Kudos.Network.Simulation
{
    public sealed class InterestManagement
    {
        private readonly KudosNetworkManager _manager;

        /// <summary>Per-recipient set of NetworkIds currently in the NEAR tier.</summary>
        private readonly Dictionary<PlayerId, HashSet<uint>> _nearSets =
            new Dictionary<PlayerId, HashSet<uint>>();

        // Reused per-tick to avoid allocation.
        private readonly List<KudosObject> _scratchEntered = new List<KudosObject>(16);
        private readonly List<KudosObject> _scratchFar = new List<KudosObject>(64);

        public InterestManagement(KudosNetworkManager manager) => _manager = manager;

        public bool Enabled => _manager.EnableInterestManagement && _manager.IsHost;

        // ------------------------------------------------------------------ queries

        /// <summary>
        /// Should `obj`'s deltas reach `recipient` this tick? Updates the near-set
        /// and reports a far->near transition (caller must send full state then).
        /// Unknown positions (no avatar yet, e.g. mid-join) fail open: include.
        /// </summary>
        public bool ShouldReceiveDeltas(KudosPeer recipient, KudosObject obj, out bool justEnteredNear)
        {
            justEnteredNear = false;
            if (!Enabled) return true;

            var near = GetNearSet(recipient.PlayerId);
            bool wasNear = near.Contains(obj.NetworkId.Value);
            bool isNear = IsNear(recipient.PlayerId, obj);

            if (isNear && !wasNear)
            {
                near.Add(obj.NetworkId.Value);
                justEnteredNear = true;
            }
            else if (!isNear && wasNear)
            {
                // Hysteresis: only demote once clearly outside (radius * 1.2) to
                // stop objects flip-flopping on the boundary.
                if (!IsNear(recipient.PlayerId, obj, 1.2f))
                    near.Remove(obj.NetworkId.Value);
                else
                    isNear = true;
            }
            return isNear;
        }

        private bool IsNear(PlayerId viewer, KudosObject obj, float radiusScale = 1f)
        {
            var head = Components.KudosAvatar.GetHeadPosition(viewer);
            if (!head.HasValue) return true; // no avatar yet -> fail open
            float r = _manager.InterestRadius * radiusScale;
            return (obj.transform.position - head.Value).sqrMagnitude <= r * r;
        }

        private HashSet<uint> GetNearSet(PlayerId player)
        {
            if (!_nearSets.TryGetValue(player, out var set))
                _nearSets[player] = set = new HashSet<uint>();
            return set;
        }

        // ------------------------------------------------------------------ far tier

        /// <summary>Called by the manager once per tick on the host.</summary>
        public void HostTick(int currentTick)
        {
            if (!Enabled) return;
            if (_manager.FarRefreshIntervalTicks <= 0) return;
            if (currentTick % _manager.FarRefreshIntervalTicks != 0) return;

            foreach (var peer in _manager.Peers.Values)
            {
                if (peer.IsLocal || peer.ConnectionId < 0 || peer.IsAway) continue;

                _scratchFar.Clear();
                var near = GetNearSet(peer.PlayerId);
                foreach (var obj in _manager.Objects)
                {
                    if (obj.Authority == peer.PlayerId) continue;       // they own it - their values win
                    if (near.Contains(obj.NetworkId.Value)) continue;   // near tier handles it
                    _scratchFar.Add(obj);
                }
                if (_scratchFar.Count > 0)
                    SendFullStateRefresh(peer, _scratchFar);
            }
        }

        /// <summary>
        /// Immediate catch-up for objects that just crossed into a recipient's
        /// radius (called from ReplicationSystem during relay/send).
        /// </summary>
        public void SendEnteredCatchUp(KudosPeer recipient, List<KudosObject> entered)
        {
            if (entered.Count > 0) SendFullStateRefresh(recipient, entered);
        }

        /// <summary>
        /// Packet: [StateRefresh][count] then per object [netId][byteLen][full fields].
        /// Length prefix lets receivers skip objects they don't know yet.
        /// Sent on the Reliable channel: refreshes are rare and must not be lost,
        /// or a far object could stay stale for another full interval.
        /// </summary>
        private void SendFullStateRefresh(KudosPeer recipient, List<KudosObject> objects)
        {
            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.StateRefresh);
            w.WriteVarUInt((uint)objects.Count);

            var scratch = KudosWriter.Rent();
            foreach (var obj in objects)
            {
                scratch.Reset();
                obj.SerializeFull(scratch);
                var blob = scratch.ToSegment();

                w.WriteVarUInt(obj.NetworkId.Value);
                w.WriteVarUInt((uint)blob.Count);
                w.WriteBytes(blob.Array, blob.Offset, blob.Count);
            }
            KudosWriter.Return(scratch);

            _manager.Transport.Send(recipient.ConnectionId, w.ToSegment(), KudosChannel.Reliable);
            KudosWriter.Return(w);
        }

        /// <summary>Receiver side (clients): apply full values for known, non-owned objects.</summary>
        public void HandleStateRefresh(KudosReader reader)
        {
            uint count = reader.ReadVarUInt();
            for (uint i = 0; i < count; i++)
            {
                var netId = new NetworkId(reader.ReadVarUInt());
                int byteLen = (int)reader.ReadVarUInt();

                if (_manager.TryGetObject(netId, out var obj) && !obj.HasAuthority)
                    obj.DeserializeFull(reader);
                else
                    reader.Skip(byteLen); // unknown (spawn in flight) or ours - skip cleanly
            }
        }

        // ------------------------------------------------------------------ lifecycle

        public void OnPeerRemoved(PlayerId player) => _nearSets.Remove(player);
        public void OnObjectDespawned(NetworkId id)
        {
            foreach (var set in _nearSets.Values) set.Remove(id.Value);
        }
        public void Clear() => _nearSets.Clear();

        internal List<KudosObject> RentEnteredList() { _scratchEntered.Clear(); return _scratchEntered; }
    }
}
