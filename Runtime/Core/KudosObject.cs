// Kudos Network Solution (KNS)
// KudosObject - identity component for every networked GameObject.
// (Fusion: NetworkObject | Mirror: NetworkIdentity | Coherence: CoherenceSync)
//
// Collects every ISyncField declared on sibling KudosBehaviours, in a
// deterministic order, so state packets are just [objectId][dirtyMask][values].

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Kudos.Network.Serialization;
using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network
{
    [DisallowMultipleComponent]
    public sealed class KudosObject : MonoBehaviour
    {
        [Tooltip("Stable id linking this prefab across peers. Auto-assigned by the KNS editor tooling.")]
        public uint PrefabHash;

        [Tooltip("Peer = owned by a player (avatars, props). Scene = owned by the current host.")]
        public AuthorityKind AuthorityKind = AuthorityKind.Peer;

        public NetworkId NetworkId { get; internal set; }
        public PlayerId Authority { get; internal set; } = PlayerId.None;

        public bool IsSpawned => NetworkId.IsValid;
        public bool HasAuthority => IsSpawned && KudosNetworkManager.Instance != null
                                    && Authority == KudosNetworkManager.Instance.LocalPlayerId;

        /// <summary>Fires whenever authority changes hands (oldAuthority, newAuthority).</summary>
        public event Action<PlayerId, PlayerId> OnAuthorityChanged;

        internal KudosBehaviour[] Behaviours { get; private set; }
        internal ISyncField[] SyncFields { get; private set; }   // flattened, deterministic order
        internal string[] SyncFieldNames { get; private set; }   // "Behaviour.Field", parallel to SyncFields

        // ---- editor / tooling read-only views (used by the runtime inspector) ----
        public System.Collections.Generic.IReadOnlyList<ISyncField> DebugSyncFields => SyncFields;
        public System.Collections.Generic.IReadOnlyList<string> DebugSyncFieldNames => SyncFieldNames;
        public ulong DebugDirtyMask
        {
            get
            {
                if (SyncFields == null) return 0;
                ulong mask = 0;
                for (int i = 0; i < SyncFields.Length; i++)
                    if (SyncFields[i].IsDirty) mask |= 1UL << i;
                return mask;
            }
        }

        private bool _bound;

        // ------------------------------------------------------------------ binding

        /// <summary>
        /// Discover behaviours + sync fields. Field order = (component order on the
        /// GameObject, then field declaration order) which is identical across peers
        /// because they instantiate the same prefab.
        /// NOTE: reflection runs ONCE per prefab type (cached) - not per instance.
        /// </summary>
        internal void Bind()
        {
            if (_bound) return;
            _bound = true;

            Behaviours = GetComponentsInChildren<KudosBehaviour>(true);
            var fields = new List<ISyncField>();
            var names = new List<string>();
            foreach (var behaviour in Behaviours)
            {
                behaviour.Object = this;
                foreach (var fieldInfo in GetSyncFieldInfos(behaviour.GetType()))
                {
                    var field = (ISyncField)fieldInfo.GetValue(behaviour);
                    if (field == null)
                    {
                        Debug.LogError($"[KNS] SyncVar field '{fieldInfo.Name}' on {behaviour.GetType().Name} is null. Initialise it inline: `public SyncVar<int> X = new SyncVar<int>();`", behaviour);
                        continue;
                    }
                    fields.Add(field);
                    names.Add($"{behaviour.GetType().Name}.{fieldInfo.Name}");
                }
            }
            SyncFields = fields.ToArray();
            SyncFieldNames = names.ToArray();

            if (SyncFields.Length > 64)
                Debug.LogError($"[KNS] {name} has {SyncFields.Length} SyncVars; the per-object dirty mask is 64 bits. Split the object.", this);
        }

        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();

        private static FieldInfo[] GetSyncFieldInfos(Type type)
        {
            if (FieldCache.TryGetValue(type, out var cached)) return cached;
            var infos = type
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => typeof(ISyncField).IsAssignableFrom(f.FieldType))
                .OrderBy(f => f.MetadataToken)  // declaration order
                .ToArray();
            FieldCache[type] = infos;
            return infos;
        }

        // ------------------------------------------------------------------ replication

        internal bool AnyDirty()
        {
            for (int i = 0; i < SyncFields.Length; i++)
                if (SyncFields[i].IsDirty) return true;
            return false;
        }

        /// <summary>Delta write: 64-bit dirty mask followed by dirty field payloads.</summary>
        internal void SerializeDelta(KudosWriter writer)
        {
            ulong mask = 0;
            for (int i = 0; i < SyncFields.Length; i++)
                if (SyncFields[i].IsDirty) mask |= 1UL << i;

            writer.WriteULong(mask);
            for (int i = 0; i < SyncFields.Length; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                SyncFields[i].Serialize(writer);
                SyncFields[i].ClearDirty();
            }
        }

        internal void DeserializeDelta(KudosReader reader)
        {
            ulong mask = reader.ReadULong();
            for (int i = 0; i < SyncFields.Length; i++)
                if ((mask & (1UL << i)) != 0)
                    SyncFields[i].Deserialize(reader);
        }

        /// <summary>Full write - spawn payloads, late-join sync, host-migration seeding.</summary>
        internal void SerializeFull(KudosWriter writer)
        {
            for (int i = 0; i < SyncFields.Length; i++)
                SyncFields[i].SerializeFull(writer);
        }

        internal void DeserializeFull(KudosReader reader)
        {
            for (int i = 0; i < SyncFields.Length; i++)
                SyncFields[i].Deserialize(reader);
        }

        // ------------------------------------------------------------------ authority API

        /// <summary>
        /// Ask the host for authority over this object (e.g. the player grabbed it).
        /// Result arrives via OnAuthorityChanged, or silently nothing if denied.
        /// (Coherence's authority-transfer flow, simplified.)
        /// </summary>
        public void RequestAuthority() => KudosNetworkManager.Instance.RequestAuthority(this);

        /// <summary>Authority holder can hand the object back to scene/host ownership.</summary>
        public void ReleaseAuthority() => KudosNetworkManager.Instance.ReleaseAuthority(this);

        internal void SetAuthority(PlayerId newAuthority)
        {
            if (Authority == newAuthority) return;
            var old = Authority;
            Authority = newAuthority;
            OnAuthorityChanged?.Invoke(old, newAuthority);
            foreach (var b in Behaviours)
            {
                if (Authority == KudosNetworkManager.Instance.LocalPlayerId) b.OnGainedAuthority();
                else if (old == KudosNetworkManager.Instance.LocalPlayerId) b.OnLostAuthority();
            }
        }

        internal void InvokeSpawned() { foreach (var b in Behaviours) b.OnSpawned(); }
        internal void InvokeDespawned() { foreach (var b in Behaviours) b.OnDespawned(); }
    }
}
