// =====================================================================
//  KNS EXAMPLE 5 — Nameplates (reading replicated avatar state)
// ---------------------------------------------------------------------
//  KudosAvatar already replicates a DisplayName SyncVar. This script,
//  placed on the avatar prefab (with a world-space TextMesh child),
//  shows the name above remote players' heads and hides it on your own.
//
//  Setup: add to your avatar prefab, assign a TextMesh child, done.
//
//  Demonstrates:
//    • Reading another component's SyncVar + OnChanged
//    • Using OnSpawned to branch local vs remote presentation
//    • Billboarding toward the local camera
// =====================================================================

using Kudos.Network;
using Kudos.Network.Rpc;
using Kudos.Network.State;
using Kudos.Network.Components;
using UnityEngine;

namespace Kudos.Network.Examples
{
    [RequireComponent(typeof(KudosAvatar))]
    public class PlayerNameplate : KudosBehaviour
    {
        [Tooltip("World-space text above the avatar's head.")]
        public TextMesh Label;

        private KudosAvatar _avatar;

        public override void OnSpawned()
        {
            _avatar = GetComponent<KudosAvatar>();

            // You don't need to see your own nameplate.
            if (HasAuthority)
            {
                if (Label != null) Label.gameObject.SetActive(false);
                return;
            }

            Apply(_avatar.DisplayName.Value);
            _avatar.DisplayName.OnChanged += (_, newName) => Apply(newName);
        }

        private void Apply(string name)
        {
            if (Label != null) Label.text = string.IsNullOrEmpty(name) ? "…" : name;
        }

        private void LateUpdate()
        {
            if (Label == null || !Label.gameObject.activeSelf) return;

            // Face the local camera.
            var cam = Camera.main;
            if (cam == null) return;
            Label.transform.rotation =
                Quaternion.LookRotation(Label.transform.position - cam.transform.position);
        }
    }
}
