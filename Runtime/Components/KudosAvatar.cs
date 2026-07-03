// Kudos Network Solution (KNS)
// KudosAvatar - the VR embodiment component: head + two hands.
//
// The authority (that avatar's player) samples XR rig transforms each tick;
// remotes interpolate. Root position rides on a sibling KudosTransform; this
// component adds the three tracked points as local-space offsets, which stay
// small numbers (nice for quantization) and are immune to root motion jitter.
//
// Also maintains the PlayerId -> avatar registry that VOIP uses to place
// voices at heads and the host uses for voice distance culling.

using System.Collections.Generic;
using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network.Components
{
    /// <summary>Attachable points on an avatar - the anchor set for network parenting.</summary>
    public enum AvatarNode : byte { None = 0, Head = 1, LeftHand = 2, RightHand = 3 }

    /// <summary>Why an avatar is hidden from the LOCAL view (flags combine).</summary>
    [System.Flags]
    public enum HideReason : byte { None = 0, Blocked = 1, PersonalSpace = 2 }

    [AddComponentMenu("Kudos/Kudos Avatar")]
    [RequireComponent(typeof(KudosTransform))]
    public class KudosAvatar : KudosBehaviour
    {
        [Header("Local rig sources (assign on the local player only - e.g. XR Origin camera & controllers)")]
        public Transform LocalHeadSource;
        public Transform LocalLeftHandSource;
        public Transform LocalRightHandSource;

        [Header("Remote visual targets (bones of the avatar model)")]
        public Transform HeadTarget;
        public Transform LeftHandTarget;
        public Transform RightHandTarget;

        [Tooltip("Which player embodies this avatar (== Object.Authority).")]
        public PlayerId OwnerId => Object != null ? Object.Authority : PlayerId.None;

        // Tracked points, local to the avatar root. ~13 bytes each on the wire.
        public SyncVar<Vector3> HeadLocalPos = new SyncVar<Vector3>();
        public SyncVar<Quaternion> HeadLocalRot = new SyncVar<Quaternion>(Quaternion.identity);
        public SyncVar<Vector3> LeftHandLocalPos = new SyncVar<Vector3>();
        public SyncVar<Quaternion> LeftHandLocalRot = new SyncVar<Quaternion>(Quaternion.identity);
        public SyncVar<Vector3> RightHandLocalPos = new SyncVar<Vector3>();
        public SyncVar<Quaternion> RightHandLocalRot = new SyncVar<Quaternion>(Quaternion.identity);

        public SyncVar<string> DisplayName = new SyncVar<string>("");

        // ---------------------------------------------------------------- registry

        private static readonly Dictionary<PlayerId, KudosAvatar> Registry = new Dictionary<PlayerId, KudosAvatar>();

        /// <summary>World-space head position of a player's avatar, or null if not spawned. Used by VOIP.</summary>
        public static Vector3? GetHeadPosition(PlayerId player)
        {
            if (Registry.TryGetValue(player, out var avatar) && avatar != null && avatar.HeadTarget != null)
                return avatar.HeadTarget.position;
            return null;
        }

        public static KudosAvatar GetAvatar(PlayerId player)
            => Registry.TryGetValue(player, out var a) ? a : null;

        /// <summary>The visual transform for an attachment node on THIS avatar.</summary>
        public Transform GetNode(AvatarNode node)
        {
            switch (node)
            {
                case AvatarNode.Head: return HeadTarget;
                case AvatarNode.LeftHand: return LeftHandTarget;
                case AvatarNode.RightHand: return RightHandTarget;
                default: return null;
            }
        }

        /// <summary>Resolve a (player, node) pair to a live transform, or null. Used by network parenting.</summary>
        public static Transform GetNodeTransform(PlayerId player, AvatarNode node)
        {
            var avatar = GetAvatar(player);
            return avatar != null ? avatar.GetNode(node) : null;
        }

        // ---------------------------------------------------------------- local visibility (moderation)

        private HideReason _hideReasons = HideReason.None;
        private Renderer[] _allRenderers;
        private Renderer[] _headRenderers;

        /// <summary>
        /// Hide/show this avatar in the LOCAL view only (block, personal-space
        /// bubble). Purely presentational - replication continues untouched, and
        /// the hidden player is never notified.
        /// </summary>
        public void SetLocallyHidden(HideReason reason, bool hidden)
        {
            var before = _hideReasons;
            if (hidden) _hideReasons |= reason; else _hideReasons &= ~reason;
            if (_hideReasons != before) ApplyVisibility();
        }

        public bool IsLocallyHidden => _hideReasons != HideReason.None;

        private void CacheRenderers()
        {
            _allRenderers = GetComponentsInChildren<Renderer>(true);
            _headRenderers = HeadTarget != null
                ? HeadTarget.GetComponentsInChildren<Renderer>(true)
                : System.Array.Empty<Renderer>();
        }

        private void ApplyVisibility()
        {
            if (_allRenderers == null) CacheRenderers();
            bool visible = _hideReasons == HideReason.None;
            foreach (var r in _allRenderers) if (r != null) r.enabled = visible;
            // The local player's own head stays hidden regardless (camera clipping).
            if (visible && HasAuthority)
                foreach (var r in _headRenderers) if (r != null) r.enabled = false;
        }

        // ---------------------------------------------------------------- lifecycle

        public override void OnSpawned()
        {
            Registry[OwnerId] = this;
            Object.OnAuthorityChanged += (oldA, newA) =>
            {
                Registry.Remove(oldA);
                Registry[newA] = this;
            };

            CacheRenderers();

            if (HasAuthority)
            {
                DisplayName.Value = Runner.Peers.TryGetValue(Runner.LocalPlayerId, out var me)
                    ? me.DisplayName : "Player";
            }
            else if (Moderation.KudosModeration.IsBlocked(OwnerId))
            {
                // Blocked before they (re)spawned - stay hidden.
                _hideReasons |= HideReason.Blocked;
            }

            ApplyVisibility(); // also hides the local player's own head (camera clipping)
        }

        public override void OnDespawned()
        {
            if (Registry.TryGetValue(OwnerId, out var current) && current == this)
                Registry.Remove(OwnerId);
        }

        // ---------------------------------------------------------------- tick

        public override void NetworkFixedUpdate(float deltaTime)
        {
            if (!HasAuthority) return;

            if (LocalHeadSource != null)
            {
                HeadLocalPos.Value = transform.InverseTransformPoint(LocalHeadSource.position);
                HeadLocalRot.Value = Quaternion.Inverse(transform.rotation) * LocalHeadSource.rotation;
            }
            if (LocalLeftHandSource != null)
            {
                LeftHandLocalPos.Value = transform.InverseTransformPoint(LocalLeftHandSource.position);
                LeftHandLocalRot.Value = Quaternion.Inverse(transform.rotation) * LocalLeftHandSource.rotation;
            }
            if (LocalRightHandSource != null)
            {
                RightHandLocalPos.Value = transform.InverseTransformPoint(LocalRightHandSource.position);
                RightHandLocalRot.Value = Quaternion.Inverse(transform.rotation) * LocalRightHandSource.rotation;
            }
        }

        private void LateUpdate()
        {
            // Drive the visual targets. Local player: directly from the rig (zero
            // latency). Remotes: from SyncVars (KudosTransform interpolates the root;
            // tracked-point smoothing at 20Hz is a simple exponential follow).
            if (HasAuthority)
            {
                Apply(HeadTarget, LocalHeadSource);
                Apply(LeftHandTarget, LocalLeftHandSource);
                Apply(RightHandTarget, LocalRightHandSource);
            }
            else
            {
                Follow(HeadTarget, HeadLocalPos.Value, HeadLocalRot.Value);
                Follow(LeftHandTarget, LeftHandLocalPos.Value, LeftHandLocalRot.Value);
                Follow(RightHandTarget, RightHandLocalPos.Value, RightHandLocalRot.Value);
            }
        }

        private static void Apply(Transform target, Transform source)
        {
            if (target == null || source == null) return;
            target.SetPositionAndRotation(source.position, source.rotation);
        }

        private void Follow(Transform target, Vector3 localPos, Quaternion localRot)
        {
            if (target == null) return;
            var worldPos = transform.TransformPoint(localPos);
            var worldRot = transform.rotation * localRot;
            float k = 1f - Mathf.Exp(-20f * Time.deltaTime); // snappy exponential smoothing
            target.position = Vector3.Lerp(target.position, worldPos, k);
            target.rotation = Quaternion.Slerp(target.rotation, worldRot, k);
        }
    }
}
