// Kudos Network Solution (KNS)
// KudosObjectInspector - live SyncVar view in the Unity inspector.
//
// DESIGN LINEAGE (Fusion's runtime inspectors): select any networked object
// in play mode and see exactly what the network sees - its NetworkId, who
// holds authority, and every SyncVar's current value plus whether it is
// dirty (pending send in the next delta). This turns "why isn't my door
// syncing" from a printf session into a ten-second glance.
//
// WHY reflection for values: ISyncField deliberately has no boxed Value
// accessor (the hot path never needs one). The inspector is editor-only cold
// code, so reading the public 'Value' property via reflection is fine.

using System.Reflection;
using Kudos.Network;
using UnityEditor;
using UnityEngine;

namespace Kudos.Network.EditorTools
{
    [CustomEditor(typeof(KudosObject))]
    public class KudosObjectInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var obj = (KudosObject)target;
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Runtime network state appears here in play mode.", MessageType.None);
                return;
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Runtime Network State", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Network Id", obj.NetworkId.Value.ToString());
                EditorGUILayout.TextField("Authority", obj.Authority.IsValid ? $"Player #{obj.Authority.Value}" : "(none / host scene)");
                EditorGUILayout.TextField("Authority Kind", obj.AuthorityKind.ToString());
                EditorGUILayout.Toggle("Has Local Authority", obj.HasAuthority);
            }

            var fields = obj.DebugSyncFields;
            var names = obj.DebugSyncFieldNames;
            if (fields == null || fields.Count == 0)
            {
                EditorGUILayout.HelpBox("No SyncVars bound (object not spawned yet?).", MessageType.None);
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"SyncVars ({fields.Count})", EditorStyles.boldLabel);

            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                string name = (names != null && i < names.Count) ? names[i] : $"field[{i}]";

                // SyncVar<T>.Value is a public property; box it for display.
                object value = null;
                var prop = field.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) value = prop.GetValue(field);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(name, GUILayout.MinWidth(160));
                    EditorGUILayout.LabelField(value?.ToString() ?? "(null)", GUILayout.MinWidth(120));
                    GUI.color = field.IsDirty ? new Color(1f, 0.7f, 0.2f) : Color.gray;
                    EditorGUILayout.LabelField(field.IsDirty ? "dirty" : "clean", GUILayout.Width(44));
                    GUI.color = Color.white;
                }
            }
        }

        // Live values change every tick; repaint continuously while playing.
        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }
}
