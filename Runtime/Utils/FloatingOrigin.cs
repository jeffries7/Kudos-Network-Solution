// Kudos Network Solution (KNS)
// Floating origin (optional).
//
// DESIGN LINEAGE (Coherence): float precision degrades with distance from the
// world origin - by 4km, VR hand jitter is visible; quantized positions also
// clamp at +-8388m. Coherence solved this with per-client floating origin.
// For most Kudos social scenes (interiors, plazas) you will never need this;
// attach it only in very large open worlds.
//
// When the local player drifts beyond Threshold, everything is shifted back so
// the player is near (0,0,0). NETWORK NOTE: KNS replicates positions in a
// shared world space, so the origin offset must be re-applied around
// (de)serialization; the hook points are marked TODO(integration) - wire them
// into KudosWriter.WriteVector3Quantized / KudosReader if you enable this.

using UnityEngine;

namespace Kudos.Network.Utils
{
    [AddComponentMenu("Kudos/Floating Origin")]
    public class FloatingOrigin : MonoBehaviour
    {
        public static FloatingOrigin Instance { get; private set; }

        [Tooltip("Shift the world when the tracked target strays this far from origin.")]
        public float Threshold = 512f;

        [Tooltip("Usually the local player's XR origin / avatar root.")]
        public Transform Tracked;

        /// <summary>Cumulative shift applied so far. worldTrue = local + OriginOffset.</summary>
        public Vector3 OriginOffset { get; private set; }

        private void Awake() => Instance = this;

        private void LateUpdate()
        {
            if (Tracked == null) return;
            var pos = Tracked.position;
            if (pos.sqrMagnitude < Threshold * Threshold) return;

            var shift = pos;
            shift.y = 0f; // keep vertical intact; shift horizontally only

            foreach (var root in gameObject.scene.GetRootGameObjects())
                root.transform.position -= shift;

            OriginOffset += shift;
            Debug.Log($"[KNS] Floating origin shifted by {shift}, cumulative {OriginOffset}");

            // TODO(integration): net positions must stay in TRUE world space:
            //   outgoing: write (localPosition + OriginOffset)
            //   incoming: apply (netPosition - OriginOffset)
        }

        /// <summary>Convert a replicated (true) world position to current local space.</summary>
        public Vector3 ToLocalSpace(Vector3 trueWorld) => trueWorld - OriginOffset;

        /// <summary>Convert a local position to replicated (true) world space.</summary>
        public Vector3 ToTrueWorld(Vector3 local) => local + OriginOffset;
    }
}
