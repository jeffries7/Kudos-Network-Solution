// Kudos Network Solution (KNS)
// Replication engine.
//
// DESIGN LINEAGE (Fusion): tick-aligned delta snapshots with eventual
// consistency over an unreliable channel. Dropped packets are never resent -
// the next delta simply carries anything still dirty, and full-state sync
// covers joins/migrations. This is dramatically simpler and lower-latency
// than reliable state replication (Mirror's default), and it is why Fusion
// feels smooth on bad Wi-Fi - which is every Quest living room.
//
// FLOW (distributed authority over a star topology):
//   1. Each peer, once per tick, packs deltas for objects IT has authority
//      over and sends them to the host on the StateSync channel.
//   2. The host applies them locally and re-broadcasts to every other client.
//      * AOI OFF: the raw packet is relayed VERBATIM (zero re-serialization).
//      * AOI ON:  the host records each object's byte-span while parsing and
//        composes a per-recipient packet containing only objects inside that
//        recipient's interest radius (see InterestManagement.cs). Far objects
//        are covered by the low-rate full-state refresh tier.
//   3. Clients apply deltas for objects they do NOT have authority over.
//
// A per-sender sequence number lets receivers drop stale packets that arrive
// out of order (unreliable channel guarantees nothing).

using System;
using System.Collections.Generic;
using Kudos.Network.Serialization;
using UnityEngine;

namespace Kudos.Network.Simulation
{
    public sealed class ReplicationSystem
    {
        private readonly KudosNetworkManager _manager;

        private ushort _outgoingSequence;
        private readonly Dictionary<PlayerId, ushort> _lastSequenceFrom = new Dictionary<PlayerId, ushort>();

        // Reused span scratch for AOI filtering (zero steady-state allocation).
        private struct ObjectSpan { public KudosObject Obj; public int Offset; public int Length; }
        private readonly List<ObjectSpan> _spans = new List<ObjectSpan>(64);
        private readonly List<KudosObject> _entered = new List<KudosObject>(16);

        public ReplicationSystem(KudosNetworkManager manager) => _manager = manager;

        // ------------------------------------------------------------------ send

        /// <summary>Called once per network tick. Packs everything dirty that we own.</summary>
        public void SendDeltas()
        {
            var dirtyObjects = ListPool<KudosObject>.Rent();
            foreach (var obj in _manager.Objects)
            {
                if (!obj.HasAuthority) continue;
                if (!obj.AnyDirty()) continue;
                dirtyObjects.Add(obj);
            }

            if (dirtyObjects.Count == 0) { ListPool<KudosObject>.Return(dirtyObjects); return; }

            var writer = KudosWriter.Rent();
            ushort seq = _outgoingSequence++;
            writer.WriteByte((byte)MsgId.StateDelta);
            writer.WriteUShort(_manager.LocalPlayerId.Value);
            writer.WriteUShort(seq);
            writer.WriteVarUInt((uint)dirtyObjects.Count);

            _spans.Clear();
            foreach (var obj in dirtyObjects)
            {
                int start = writer.Position;
                writer.WriteVarUInt(obj.NetworkId.Value);
                obj.SerializeDelta(writer);
                _spans.Add(new ObjectSpan { Obj = obj, Offset = start, Length = writer.Position - start });
            }

            var packet = writer.ToSegment();
            var interest = _manager.Interest;

            if (_manager.IsHost && interest != null && interest.Enabled)
            {
                // Host origin + AOI: compose a filtered packet per recipient.
                foreach (var peer in _manager.Peers.Values)
                {
                    if (peer.IsLocal || peer.ConnectionId < 0 || peer.IsAway) continue;
                    RelayFiltered(packet, _spans, _manager.LocalPlayerId, seq, peer, interest);
                }
            }
            else
            {
                _manager.SendToHostOrBroadcast(packet, Transport.KudosChannel.StateSync);
            }

            KudosWriter.Return(writer);
            ListPool<KudosObject>.Return(dirtyObjects);
        }

        // ------------------------------------------------------------------ receive

        public void HandleStateDelta(KudosReader reader, ArraySegment<byte> rawPacket, int fromConnectionId)
        {
            var sender = new PlayerId(reader.ReadUShort());
            ushort sequence = reader.ReadUShort();

            // Drop out-of-order packets from this sender (sequenced semantics).
            if (_lastSequenceFrom.TryGetValue(sender, out var last) && !IsNewer(sequence, last))
                return;
            _lastSequenceFrom[sender] = sequence;

            var interest = _manager.Interest;
            bool aoiRelay = _manager.IsHost && interest != null && interest.Enabled;

            // AOI OFF: relay verbatim BEFORE parsing - zero re-serialization, and a
            // local parse hiccup can never stall other peers' state.
            if (_manager.IsHost && !aoiRelay)
                _manager.Transport.Broadcast(rawPacket, Transport.KudosChannel.StateSync, exceptConnectionId: fromConnectionId);

            // Parse (and, for AOI, record each object's byte span for re-packing).
            _spans.Clear();
            uint objectCount = reader.ReadVarUInt();
            for (uint i = 0; i < objectCount; i++)
            {
                int start = reader.Position;
                var netId = new NetworkId(reader.ReadVarUInt());
                if (_manager.TryGetObject(netId, out var obj) && !obj.HasAuthority)
                {
                    obj.DeserializeDelta(reader);
                }
                else
                {
                    // Unknown object (spawn packet in flight) or we own it - must still
                    // consume the payload to keep the reader aligned.
                    SkipDelta(reader, obj);
                }
                if (aoiRelay && obj != null)
                    _spans.Add(new ObjectSpan { Obj = obj, Offset = start, Length = reader.Position - start });
            }

            // AOI ON: compose per-recipient packets from the recorded spans.
            if (aoiRelay)
            {
                foreach (var peer in _manager.Peers.Values)
                {
                    if (peer.IsLocal || peer.ConnectionId < 0 || peer.IsAway) continue;
                    if (peer.ConnectionId == fromConnectionId) continue;     // never echo to origin
                    RelayFiltered(rawPacket, _spans, sender, sequence, peer, interest);
                }
            }
        }

        /// <summary>
        /// Build + send one recipient's filtered StateDelta from recorded spans.
        /// Objects the recipient owns are excluded (their values win); objects that
        /// just crossed into their radius additionally get a full-state catch-up.
        /// </summary>
        private void RelayFiltered(ArraySegment<byte> source, List<ObjectSpan> spans,
            PlayerId sender, ushort sequence, KudosPeer recipient, InterestManagement interest)
        {
            _entered.Clear();
            var w = KudosWriter.Rent();
            w.WriteByte((byte)MsgId.StateDelta);
            w.WriteUShort(sender.Value);
            w.WriteUShort(sequence);

            // Count objects first (cheap - span list is small), then write.
            int included = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                var span = spans[i];
                if (span.Obj.Authority == recipient.PlayerId) continue;
                if (interest.ShouldReceiveDeltas(recipient, span.Obj, out bool enteredNear))
                {
                    included++;
                    if (enteredNear) _entered.Add(span.Obj);
                }
                else
                {
                    span.Length = -span.Length; // mark excluded (sign bit trick, restored below)
                    spans[i] = span;
                }
            }

            if (included > 0)
            {
                w.WriteVarUInt((uint)included);
                for (int i = 0; i < spans.Count; i++)
                {
                    var span = spans[i];
                    if (span.Length < 0) continue;
                    w.WriteBytes(source.Array, span.Offset, span.Length);
                }
                _manager.Transport.Send(recipient.ConnectionId, w.ToSegment(), Transport.KudosChannel.StateSync);
            }
            KudosWriter.Return(w);

            // Restore excluded marks for the next recipient's pass.
            for (int i = 0; i < spans.Count; i++)
            {
                var span = spans[i];
                if (span.Length < 0) { span.Length = -span.Length; spans[i] = span; }
            }

            // Immediate full-state catch-up for far->near transitions.
            if (_entered.Count > 0) interest.SendEnteredCatchUp(recipient, _entered);
        }

        private static void SkipDelta(KudosReader reader, KudosObject knownObject)
        {
            if (knownObject != null)
            {
                // We know the schema - deserialize into a throwaway pass.
                // (Authority race: our values win; remote echo is discarded.)
                ulong mask = reader.ReadULong();
                for (int i = 0; i < knownObject.SyncFields.Length; i++)
                    if ((mask & (1UL << i)) != 0)
                        knownObject.SyncFields[i].GetType(); // schema known but value ignored:
                // NOTE: proper skip needs field sizes; simplest correct approach is a
                // scratch deserialize. Skeleton keeps it explicit:
                // -> production: deserialize into a pooled shadow instance.
                throw new NotSupportedException(
                    "[KNS] Received delta for object we have authority over - shadow-skip not yet implemented. " +
                    "This occurs only during authority-transfer races; see README 'Known skeleton gaps'.");
            }
            throw new FormatException(
                "[KNS] Delta for unknown object - packet ordering issue (spawn not yet applied). " +
                "Production fix: buffer unknown-object deltas for N ticks. See README 'Known skeleton gaps'.");
        }

        private static bool IsNewer(ushort incoming, ushort last)
            => (ushort)(incoming - last) < 32768; // wrap-around safe

        // ------------------------------------------------------------------ full state (late join / host migration)

        public void WriteFullState(KudosWriter writer)
        {
            writer.WriteVarUInt((uint)_manager.Objects.Count);
            foreach (var obj in _manager.Objects)
            {
                writer.WriteVarUInt(obj.NetworkId.Value);
                writer.WriteUInt(obj.PrefabHash);
                writer.WriteUShort(obj.Authority.Value);
                writer.WriteByte((byte)obj.AuthorityKind);
                obj.SerializeFull(writer);
            }
        }

        public void ReadFullState(KudosReader reader)
        {
            uint count = reader.ReadVarUInt();
            for (uint i = 0; i < count; i++)
            {
                var netId = new NetworkId(reader.ReadVarUInt());
                uint prefabHash = reader.ReadUInt();
                var authority = new PlayerId(reader.ReadUShort());
                var kind = (AuthorityKind)reader.ReadByte();

                var obj = _manager.TryGetObject(netId, out var existing)
                    ? existing
                    : _manager.InstantiateFromPrefabHash(prefabHash, netId, authority, kind);

                if (obj != null) obj.DeserializeFull(reader);
            }
        }
    }

    /// <summary>Tiny list pool to keep per-tick allocations at zero.</summary>
    internal static class ListPool<T>
    {
        private static readonly Stack<List<T>> Pool = new Stack<List<T>>();
        public static List<T> Rent() => Pool.Count > 0 ? Pool.Pop() : new List<T>(32);
        public static void Return(List<T> list) { list.Clear(); Pool.Push(list); }
    }
}
