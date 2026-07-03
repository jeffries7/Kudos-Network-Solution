// Kudos Network Solution (KNS)
// KudosTransform - networked transform with remote interpolation and
// network parenting.
//
// DESIGN LINEAGE (Fusion NetworkTransform + Coherence interpolation):
// The authority samples its transform every network tick into SyncVars
// (quantized on the wire). Remote peers do NOT snap to incoming values -
// they buffer them and render ~2 ticks in the past, interpolating between
// the two snapshots that bracket render time. At 20Hz that is 100ms of
// display latency, invisible for slow-paced social movement, and it makes
// 20Hz data look perfectly smooth at 90/120fps headset refresh.
//
// NETWORK PARENTING (held objects):
// A grabbed prop synced in world space always trails the moving avatar that
// holds it - the prop and the hand replicate on independent 100ms-delayed
// streams that never quite agree. Parenting fixes this at the source: while
// parented to (player, AvatarNode), NetPosition/NetRotation are LOCAL to
// that node. Remotes resolve the node on THEIR OWN interpolated avatar each
// frame and compose - the prop is glued to the hand, pixel-perfect, and a
// static grip costs zero bandwidth because the local offset never changes.

using System.Collections.Generic;
using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network.Components
{
    [AddComponentMenu("Kudos/Kudos Transform")]
    public class KudosTransform : KudosBehaviour
    {
        [Tooltip("Render this many ticks behind the newest snapshot (interpolation window).")]
        [Range(1f, 4f)] public float InterpolationDelayTicks = 2f;

        [Tooltip("Snap instead of interpolate when a gap exceeds this distance (teleports). World-space mode only.")]
        public float TeleportThreshold = 3f;

        // ---- replicated state -------------------------------------------------
        // DECLARATION ORDER MATTERS: parent fields come FIRST so that when a
        // delta carries a parent change plus new (now local-space) coordinates,
        // remotes apply the parent before interpreting the coordinates.
        public SyncVar<ushort> ParentPlayerRaw = new SyncVar<ushort>(ushort.MaxValue);
        public SyncVar<byte> ParentNodeRaw = new SyncVar<byte>((byte)AvatarNode.None);

        // World space when unparented; local to the parent node when parented.
        public SyncVar<Vector3> NetPosition = new SyncVar<Vector3>();
        public SyncVar<Quaternion> NetRotation = new SyncVar<Quaternion>(Quaternion.identity);

        public PlayerId ParentPlayer => new PlayerId(ParentPlayerRaw.Value);
        public AvatarNode ParentNode => (AvatarNode)ParentNodeRaw.Value;
        public bool HasNetworkParent => ParentPlayer.IsValid && ParentNode != AvatarNode.None;

        private struct Snapshot
        {
            public float Time;
            public Vector3 Position;    // in the space active when sampled
            public Quaternion Rotation;
        }

        private readonly Queue<Snapshot> _buffer = new Queue<Snapshot>();
        private Snapshot _from, _to;
        private bool _hasSegment;

        // ================================================================== api

        /// <summary>
        /// Authority only: attach this object to an avatar node. From now on the
        /// wire carries coordinates local to that node; remotes glue the object to
        /// their own interpolated view of the node. Call from grab logic.
        /// </summary>
        public void SetNetworkParent(PlayerId player, AvatarNode node)
        {
            if (!HasAuthority) { Debug.LogError("[KNS] SetNetworkParent requires authority"); return; }
            ParentPlayerRaw.Value = player.Value;
            ParentNodeRaw.Value = (byte)node;
            SampleAuthority(); // publish coordinates in the new space immediately
        }

        /// <summary>Authority only: detach - back to world-space sync, keeping the current world pose.</summary>
        public void ClearNetworkParent()
        {
            if (!HasAuthority) { Debug.LogError("[KNS] ClearNetworkParent requires authority"); return; }
            ParentPlayerRaw.Value = PlayerId.None.Value;
            ParentNodeRaw.Value = (byte)AvatarNode.None;
            SampleAuthority();
        }

        // ================================================================== lifecycle

        public override void OnSpawned()
        {
            NetPosition.OnChanged += (_, __) => PushSnapshot();
            NetRotation.OnChanged += (_, __) => PushSnapshot();

            // Space changed -> buffered snapshots are in the wrong coordinates. Drop
            // them and snap; a one-frame pop at the moment of grab/release is
            // imperceptible (the hand is right there anyway).
            ParentPlayerRaw.OnChanged += (_, __) => FlushBuffer();
            ParentNodeRaw.OnChanged += (_, __) => FlushBuffer();

            // Initial placement for remotes.
            if (!HasAuthority) ApplyImmediate(NetPosition.Value, NetRotation.Value);
        }

        private void FlushBuffer()
        {
            if (HasAuthority) return;
            _buffer.Clear();
            _hasSegment = false;
            ApplyImmediate(NetPosition.Value, NetRotation.Value);
        }

        // ================================================================== authority side

        public override void NetworkFixedUpdate(float deltaTime)
        {
            if (!HasAuthority) return;
            SampleAuthority();
        }

        private void SampleAuthority()
        {
            var parent = ResolveParent();
            if (parent != null)
            {
                // Quantization-aware equality in SyncVar: a static grip = zero traffic.
                NetPosition.Value = parent.InverseTransformPoint(transform.position);
                NetRotation.Value = Quaternion.Inverse(parent.rotation) * transform.rotation;
            }
            else
            {
                NetPosition.Value = transform.position;
                NetRotation.Value = transform.rotation;
            }
        }

        // ================================================================== remote side

        private Transform ResolveParent()
            => HasNetworkParent ? KudosAvatar.GetNodeTransform(ParentPlayer, ParentNode) : null;

        private void PushSnapshot()
        {
            if (HasAuthority) return;
            _buffer.Enqueue(new Snapshot
            {
                Time = Time.time,
                Position = NetPosition.Value,
                Rotation = NetRotation.Value
            });
        }

        private void Update()
        {
            if (HasAuthority || Runner == null) return;

            float delay = InterpolationDelayTicks / Runner.TickRate;
            float renderTime = Time.time - delay;

            // Advance the segment while the buffer has snapshots older than render time.
            while (_buffer.Count > 0 && _buffer.Peek().Time <= renderTime)
            {
                _from = _hasSegment ? _to : _buffer.Peek();
                _to = _buffer.Dequeue();
                _hasSegment = true;
            }
            if (!_hasSegment)
            {
                // No motion segment yet - while parented, still glue to the (moving)
                // node using the last known local offset.
                if (HasNetworkParent) ApplyImmediate(NetPosition.Value, NetRotation.Value);
                return;
            }

            // Teleport detection only makes sense in world space; parented local
            // offsets are tiny and a large jump there IS meaningful motion.
            if (!HasNetworkParent && Vector3.Distance(_from.Position, _to.Position) > TeleportThreshold)
            {
                ApplyImmediate(_to.Position, _to.Rotation);
                return;
            }

            float span = Mathf.Max(_to.Time - _from.Time, 0.0001f);
            float t = Mathf.Clamp01((renderTime - _from.Time) / span);
            ApplyImmediate(
                Vector3.LerpUnclamped(_from.Position, _to.Position, t),
                Quaternion.SlerpUnclamped(_from.Rotation, _to.Rotation, t));
        }

        /// <summary>Place the object from replicated-space coordinates (composes through the parent node if any).</summary>
        private void ApplyImmediate(Vector3 pos, Quaternion rot)
        {
            var parent = ResolveParent();
            if (parent != null)
                transform.SetPositionAndRotation(parent.TransformPoint(pos), parent.rotation * rot);
            else if (HasNetworkParent)
                return; // parent avatar not spawned yet - hold pose until it is
            else
                transform.SetPositionAndRotation(pos, rot);
        }
    }
}
