// =====================================================================
//  KNS EXAMPLE 2 — A shared world object (SyncVar + OnChanged + RpcHost)
// ---------------------------------------------------------------------
//  A door anyone can open. The door is a scene object owned by the host
//  (AuthorityKind.Scene), so non-owners *request* the toggle via RpcHost
//  and the host flips the SyncVar. Everyone's OnChanged hook then plays
//  the animation locally. Late joiners get the correct state for free
//  (full-state sync on join), and the pattern survives host migration
//  because RpcHost always targets the *current* host.
//
//  Setup: put this + a KudosObject on a door in the scene.
//
//  Demonstrates:
//    • SyncVar<bool> with an OnChanged hook driving visuals
//    • The "request via RpcHost, host mutates state" pattern
//    • Why you never animate directly on the requester
// =====================================================================

using Kudos.Network;
using Kudos.Network.Rpc;
using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network.Examples
{
    public class SyncedDoor : KudosBehaviour
    {
        [Tooltip("Rotated when the door opens/closes.")]
        public Transform Hinge;
        public float OpenAngle = 110f;
        public float SwingSpeed = 4f;

        // Replicated state. The host owns this; everyone else just reads it.
        public SyncVar<bool> IsOpen = new SyncVar<bool>();

        private float _currentAngle;

        public override void OnSpawned()
        {
            // React to state changes — this fires on EVERY peer, including
            // for the initial value delivered to late joiners.
            IsOpen.OnChanged += (oldValue, newValue) =>
                Debug.Log($"[Door {NetworkId.Value}] {(newValue ? "opened" : "closed")}");
        }

        /// <summary>Wire this to your interaction system (poke button, XR ray, OnMouseDown…).</summary>
        public void Interact()
        {
            // Don't flip local state here! Ask the owner. If we ARE the
            // owner (host, for scene objects), the RPC just runs locally.
            RpcHost(nameof(RequestToggle));
        }

        [KudosRpc]
        public void RequestToggle()
        {
            // Runs on the host only. Guard anyway — cheap and explicit.
            if (!HasAuthority) return;

            IsOpen.Value = !IsOpen.Value;   // marks dirty → replicated next tick
        }

        private void Update()
        {
            // Purely local presentation: ease toward the replicated state.
            float target = IsOpen.Value ? OpenAngle : 0f;
            _currentAngle = Mathf.Lerp(_currentAngle, target, Time.deltaTime * SwingSpeed);
            if (Hinge != null)
                Hinge.localRotation = Quaternion.Euler(0f, _currentAngle, 0f);
        }

        // Handy for desktop testing: click the door to toggle it.
        private void OnMouseDown() => Interact();
    }
}
