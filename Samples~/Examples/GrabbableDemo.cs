// =====================================================================
//  KNS EXAMPLE 4 — Grabbing props (authority transfer)
// ---------------------------------------------------------------------
//  KudosGrabbable already implements the Coherence-style flow:
//  Grab() → RequestAuthority() → host grants → you own it and your
//  transform updates drive it; Release() hands authority back.
//
//  This script is the thin glue between an interaction system and that
//  flow, with a desktop fallback (click to grab, click to drop) so you
//  can test authority handover with two editor/build instances before
//  wiring up your XR interaction toolkit.
//
//  Setup: prop needs KudosObject + KudosTransform + KudosGrabbable +
//  a Rigidbody + a Collider. Add this script for the demo input.
//
//  Demonstrates:
//    • Grab()/Release() and reading IsHeld / Holder
//    • OnGainedAuthority/OnLostAuthority timing via grabbable events
//    • While held: move the object ONLY if you have authority
// =====================================================================

using Kudos.Network;
using Kudos.Network.Rpc;
using Kudos.Network.State;
using Kudos.Network.Components;
using UnityEngine;

namespace Kudos.Network.Examples
{
    [RequireComponent(typeof(KudosGrabbable))]
    public class GrabbableDemo : KudosBehaviour
    {
        [Tooltip("Where a held object sits relative to the local camera (desktop demo only).")]
        public Vector3 HeldOffset = new Vector3(0f, -0.2f, 1.2f);

        private KudosGrabbable _grabbable;
        private bool _heldByMe;

        private void Awake() => _grabbable = GetComponent<KudosGrabbable>();

        // ---- Input glue (replace with your XR interactor callbacks) ----

        private void OnMouseDown()
        {
            if (!_grabbable.IsHeld)
            {
                _grabbable.Grab();          // async: authority arrives a moment later
            }
            else if (_heldByMe)
            {
                _grabbable.Release();
                _heldByMe = false;
            }
            // Held by someone else → do nothing (their grip wins).
        }

        // ---- Authority callbacks ----

        public override void OnGainedAuthority()
        {
            // Fires when the host grants our RequestAuthority.
            if (_grabbable.Holder == Runner.LocalPlayerId)
            {
                _heldByMe = true;
                Debug.Log($"[Grab] You picked up {name}");
            }
        }

        public override void OnLostAuthority()
        {
            _heldByMe = false;
        }

        // ---- Movement while held ----

        private void LateUpdate()
        {
            // Golden rule: only move objects you have authority over.
            // KudosTransform replicates our movement to everyone else.
            if (!_heldByMe || !HasAuthority) return;

            var cam = Camera.main;
            if (cam == null) return;

            transform.position = cam.transform.TransformPoint(HeldOffset);
            transform.rotation = cam.transform.rotation;
        }
    }
}
