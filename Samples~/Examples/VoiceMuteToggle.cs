// =====================================================================
//  KNS EXAMPLE 7 — Voice controls (mute + replicated speaking icon)
// ---------------------------------------------------------------------
//  Muting the mic is a single local flag (KudosVoice.MicEnabled) — no
//  network traffic while muted, because VAD/capture stops sending.
//  The "muted" ICON above your avatar, however, is state other people
//  see, so it's a SyncVar on the avatar like anything else.
//
//  Setup: add to your avatar prefab; assign an icon object (child of
//  the head). Call ToggleMute() from a menu button or controller input.
//
//  Demonstrates:
//    • KudosVoice.Instance.MicEnabled (local capture control)
//    • Combining a local device flag with a replicated SyncVar<bool>
//    • Authority check before writing a SyncVar
// =====================================================================

using Kudos.Network;
using Kudos.Network.Rpc;
using Kudos.Network.State;
using Kudos.Network.Voip;
using UnityEngine;

namespace Kudos.Network.Examples
{
    public class VoiceMuteToggle : KudosBehaviour
    {
        [Tooltip("Icon shown over the avatar's head while muted (visible to everyone).")]
        public GameObject MutedIcon;

        // Replicated so remote peers can render the muted icon.
        public SyncVar<bool> IsMuted = new SyncVar<bool>();

        public override void OnSpawned()
        {
            Apply(IsMuted.Value);
            IsMuted.OnChanged += (_, muted) => Apply(muted);
        }

        /// <summary>Bind to your mute button / controller shortcut.</summary>
        public void ToggleMute()
        {
            // Only meaningful on your own avatar.
            if (!HasAuthority) return;

            bool nowMuted = !IsMuted.Value;

            // 1. Local: stop feeding frames to the encoder at the source.
            if (KudosVoice.Instance != null)
                KudosVoice.Instance.MicEnabled = !nowMuted;

            // 2. Replicated: everyone else sees the icon.
            IsMuted.Value = nowMuted;

            Debug.Log(nowMuted ? "[Voice] Muted" : "[Voice] Unmuted");
        }

        private void Apply(bool muted)
        {
            if (MutedIcon != null) MutedIcon.SetActive(muted);
        }
    }
}
