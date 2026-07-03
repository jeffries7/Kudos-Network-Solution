// Kudos Network Solution (KNS)
// KudosModeration - social-platform safety primitives.
//
// For a social VR platform these are table stakes, and crucially they are
// almost all LOCAL: muting or blocking someone changes what YOUR client
// renders and plays - no protocol, no server, no consent from the other
// party required. Only kick involves the network (host -> target).
//
//   * Voice mute:   drop a speaker's voice frames before decode (client-side)
//   * Block:        voice mute + hide their avatar entirely (client-side)
//   * Kick:         host removes a peer from the room (see KudosNetworkManager.KickPlayer;
//                   the host also refuses re-joins from that signaling identity
//                   for the rest of the session)
//   * Personal space bubble: avatars that come within PersonalSpaceRadius of
//                   your head fade from YOUR view (the classic anti-harassment
//                   measure - they see themselves normally, you don't see them)
//
// SCOPE NOTE: mute/block sets are keyed by room-scoped PlayerId, i.e. they
// last for the session. Production wants them keyed by your platform account
// id and persisted; wire that in where marked TODO(integration).

using System;
using System.Collections.Generic;
using Kudos.Network.Components;
using UnityEngine;

namespace Kudos.Network.Moderation
{
    [AddComponentMenu("Kudos/Kudos Moderation")]
    public sealed class KudosModeration : MonoBehaviour
    {
        public static KudosModeration Instance { get; private set; }

        [Header("Personal space bubble")]
        [Tooltip("Remote avatars closer than this to your head are hidden from your view. 0 = disabled.")]
        public float PersonalSpaceRadius = 0.75f;

        [Tooltip("Re-show once they retreat past radius * this factor (hysteresis - stops flicker on the boundary).")]
        [Range(1.05f, 2f)] public float ReappearFactor = 1.3f;

        // ---- session-scoped sets (TODO(integration): key by platform account id + persist) ----
        private static readonly HashSet<ushort> VoiceMuted = new HashSet<ushort>();
        private static readonly HashSet<ushort> Blocked = new HashSet<ushort>();

        public static event Action<PlayerId, bool> OnMuteChanged;
        public static event Action<PlayerId, bool> OnBlockChanged;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        // ================================================================== mute

        /// <summary>Stop hearing this player. Local only - they are not notified.</summary>
        public static void SetVoiceMuted(PlayerId player, bool muted)
        {
            bool changed = muted ? VoiceMuted.Add(player.Value) : VoiceMuted.Remove(player.Value);
            if (changed) OnMuteChanged?.Invoke(player, muted);
        }

        /// <summary>True if voice from this player should be dropped locally (muted OR blocked).</summary>
        public static bool IsVoiceMuted(PlayerId player)
            => VoiceMuted.Contains(player.Value) || Blocked.Contains(player.Value);

        // ================================================================== block

        /// <summary>Mute + hide this player entirely. Local only.</summary>
        public static void SetBlocked(PlayerId player, bool blocked)
        {
            bool changed = blocked ? Blocked.Add(player.Value) : Blocked.Remove(player.Value);
            if (!changed) return;

            var avatar = KudosAvatar.GetAvatar(player);
            if (avatar != null) avatar.SetLocallyHidden(HideReason.Blocked, blocked);
            OnBlockChanged?.Invoke(player, blocked);
        }

        public static bool IsBlocked(PlayerId player) => Blocked.Contains(player.Value);

        // ================================================================== kick (convenience passthrough)

        /// <summary>Host only: remove a player from the room. See KudosNetworkManager.KickPlayer.</summary>
        public static void Kick(PlayerId player, string reason = "Removed by host")
            => KudosNetworkManager.Instance?.KickPlayer(player, reason);

        // ================================================================== personal space bubble

        private readonly HashSet<ushort> _inBubble = new HashSet<ushort>();

        private void LateUpdate()
        {
            if (PersonalSpaceRadius <= 0f) return;
            var net = KudosNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            Vector3? myHead = KudosAvatar.GetHeadPosition(net.LocalPlayerId);
            if (!myHead.HasValue && Camera.main != null) myHead = Camera.main.transform.position;
            if (!myHead.HasValue) return;

            float enterSqr = PersonalSpaceRadius * PersonalSpaceRadius;
            float exitR = PersonalSpaceRadius * ReappearFactor;
            float exitSqr = exitR * exitR;

            foreach (var peer in net.Peers.Values)
            {
                if (peer.IsLocal) continue;
                var avatar = KudosAvatar.GetAvatar(peer.PlayerId);
                if (avatar == null) continue;

                var theirHead = KudosAvatar.GetHeadPosition(peer.PlayerId);
                if (!theirHead.HasValue) continue;

                float sqr = (theirHead.Value - myHead.Value).sqrMagnitude;
                bool inside = _inBubble.Contains(peer.PlayerId.Value);

                if (!inside && sqr < enterSqr)
                {
                    _inBubble.Add(peer.PlayerId.Value);
                    avatar.SetLocallyHidden(HideReason.PersonalSpace, true);
                }
                else if (inside && sqr > exitSqr)
                {
                    _inBubble.Remove(peer.PlayerId.Value);
                    avatar.SetLocallyHidden(HideReason.PersonalSpace, false);
                }
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
