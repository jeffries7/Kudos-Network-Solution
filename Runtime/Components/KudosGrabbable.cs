// Kudos Network Solution (KNS)
// KudosGrabbable - shared interactable props.
//
// DESIGN LINEAGE (Coherence authority transfer): the canonical social-VR
// interaction - two people and one boombox. Whoever grabs it requests
// authority; the host arbitrates first-come-first-served; the winner
// simulates it locally with zero latency; on release, authority returns to
// the host (scene ownership) so the prop keeps replicating (and its physics
// keep settling) even when nobody holds it.
//
// Hook Grab()/Release() to your interaction system (XR Interaction Toolkit's
// selectEntered/selectExited events map 1:1).

using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network.Components
{
    [AddComponentMenu("Kudos/Kudos Grabbable")]
    [RequireComponent(typeof(KudosTransform))]
    public class KudosGrabbable : KudosBehaviour
    {
        [Tooltip("Physics sleeps on remotes; only the authority simulates.")]
        public Rigidbody Body;

        [Tooltip("Avatar node the prop glues to while held (network parenting). " +
                 "Grab(AvatarNode) overrides this per-grab for two-handed setups.")]
        public AvatarNode GrabNode = AvatarNode.RightHand;

        /// <summary>Who is currently holding this (None = free / host-owned).</summary>
        public SyncVar<ushort> HolderRaw = new SyncVar<ushort>(ushort.MaxValue);
        public PlayerId Holder => new PlayerId(HolderRaw.Value);
        public bool IsHeld => Holder.IsValid;

        private bool _wantsGrab;
        private AvatarNode _grabNode = AvatarNode.None;
        private KudosTransform _kt;

        public override void OnSpawned()
        {
            if (Body == null) Body = GetComponent<Rigidbody>();
            _kt = GetComponent<KudosTransform>();
            HolderRaw.OnChanged += (_, __) => RefreshPhysics();
            RefreshPhysics();
        }

        /// <summary>Call when the local player grabs (e.g. XRGrabInteractable.selectEntered).</summary>
        public void Grab() => Grab(GrabNode);

        /// <summary>Grab with an explicit attach node (e.g. the hand that actually selected).</summary>
        public void Grab(AvatarNode node)
        {
            if (IsHeld && Holder != Runner.LocalPlayerId) return; // someone else has it
            _wantsGrab = true;
            _grabNode = node;
            Object.RequestAuthority(); // async - OnGainedAuthority completes the grab
        }

        /// <summary>Call when the local player releases.</summary>
        public void Release()
        {
            _wantsGrab = false;
            if (!HasAuthority) return;
            HolderRaw.Value = PlayerId.None.Value;
            _kt?.ClearNetworkParent(); // back to world-space replication
            Object.ReleaseAuthority(); // back to host/scene ownership
        }

        public override void OnGainedAuthority()
        {
            if (_wantsGrab)
            {
                HolderRaw.Value = Runner.LocalPlayerId.Value;
                _wantsGrab = false;
                // Network parenting: while held, replicate LOCAL to the hand so the
                // prop stays glued to a moving avatar instead of trailing it.
                if (_grabNode != AvatarNode.None)
                    _kt?.SetNetworkParent(Runner.LocalPlayerId, _grabNode);
            }
            else if (IsHeld && Holder != Runner.LocalPlayerId)
            {
                // Authority reclaimed (e.g. holder left and host inherited): the
                // recorded holder is gone, so free the prop and unparent it.
                HolderRaw.Value = PlayerId.None.Value;
                _kt?.ClearNetworkParent();
            }
            RefreshPhysics();
        }

        public override void OnLostAuthority()
        {
            _wantsGrab = false;
            RefreshPhysics();
        }

        private void RefreshPhysics()
        {
            if (Body == null) return;
            // Authority simulates; remotes are kinematic and follow KudosTransform.
            Body.isKinematic = !HasAuthority;
        }
    }
}
