// Kudos Network Solution (KNS)
// Networked state fields.
//
// DESIGN LINEAGE:
//   * Coherence: per-field bindings, each with its own send policy. In
//     Coherence you tick checkboxes in the inspector; in KNS you declare
//     SyncVar<T> fields - discovered automatically, in declaration order.
//   * Mirror: the SyncVar concept + OnChanged hooks.
//   * Fusion: dirty-bit change tracking so snapshots only carry deltas.
//
// WHY A WRAPPER TYPE INSTEAD OF IL WEAVING / SOURCE GENERATION:
//   Mirror weaves ILPostProcessor magic into plain fields; Fusion source-
//   generates property bodies. Both add build-pipeline complexity and are the
//   #1 source of "it broke after a Unity upgrade" tickets in those ecosystems.
//   SyncVar<T> is plain C#: debuggable, IL2CPP/WebGL-safe, zero build magic.
//   The cost is writing ".Value" - acceptable for a social platform SDK.
//
// Usage inside any KudosBehaviour:
//
//     public SyncVar<int>    Score  = new SyncVar<int>();
//     public SyncVar<string> Status = new SyncVar<string>("chilling");
//
//     void Awake() => Score.OnChanged += (oldV, newV) => RefreshUI(newV);

using System;
using System.Collections.Generic;
using Kudos.Network.Serialization;
using UnityEngine;

namespace Kudos.Network.State
{
    /// <summary>Non-generic surface used by the replication system.</summary>
    public interface ISyncField
    {
        bool IsDirty { get; }
        void ClearDirty();
        void Serialize(KudosWriter writer);
        void Deserialize(KudosReader reader);
        /// <summary>Full-state write regardless of dirtiness (spawns, late joiners, host migration).</summary>
        void SerializeFull(KudosWriter writer);
    }

    public sealed class SyncVar<T> : ISyncField
    {
        private T _value;
        public bool IsDirty { get; private set; }

        /// <summary>(oldValue, newValue). Fires on local sets AND on remote updates.</summary>
        public event Action<T, T> OnChanged;

        public SyncVar() { _value = default; }
        public SyncVar(T initial) { _value = initial; }

        public T Value
        {
            get => _value;
            set
            {
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                var old = _value;
                _value = value;
                IsDirty = true;
                OnChanged?.Invoke(old, value);
            }
        }

        public void ClearDirty() => IsDirty = false;

        public void Serialize(KudosWriter writer) => KudosSerializer<T>.Write(writer, _value);
        public void SerializeFull(KudosWriter writer) => KudosSerializer<T>.Write(writer, _value);

        public void Deserialize(KudosReader reader)
        {
            var old = _value;
            _value = KudosSerializer<T>.Read(reader);
            if (!EqualityComparer<T>.Default.Equals(old, _value))
                OnChanged?.Invoke(old, _value);
        }

        public static implicit operator T(SyncVar<T> syncVar) => syncVar._value;
    }

    /// <summary>
    /// Static-generic serializer registry. Register(Write, Read) once per type;
    /// lookups after that are a static field access - no dictionaries, no boxing.
    /// Custom game types register in a RuntimeInitializeOnLoad method.
    /// </summary>
    public static class KudosSerializer<T>
    {
        public static Action<KudosWriter, T> Write;
        public static Func<KudosReader, T> Read;
    }

    public static class BuiltInSerializers
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register()
        {
            KudosSerializer<byte>.Write = (w, v) => w.WriteByte(v);
            KudosSerializer<byte>.Read = r => r.ReadByte();

            KudosSerializer<bool>.Write = (w, v) => w.WriteBool(v);
            KudosSerializer<bool>.Read = r => r.ReadBool();

            KudosSerializer<ushort>.Write = (w, v) => w.WriteUShort(v);
            KudosSerializer<ushort>.Read = r => r.ReadUShort();

            KudosSerializer<int>.Write = (w, v) => w.WriteInt(v);
            KudosSerializer<int>.Read = r => r.ReadInt();

            KudosSerializer<uint>.Write = (w, v) => w.WriteVarUInt(v);
            KudosSerializer<uint>.Read = r => r.ReadVarUInt();

            KudosSerializer<ulong>.Write = (w, v) => w.WriteULong(v);
            KudosSerializer<ulong>.Read = r => r.ReadULong();

            KudosSerializer<float>.Write = (w, v) => w.WriteFloat(v);
            KudosSerializer<float>.Read = r => r.ReadFloat();

            KudosSerializer<string>.Write = (w, v) => w.WriteString(v);
            KudosSerializer<string>.Read = r => r.ReadString();

            KudosSerializer<Vector3>.Write = (w, v) => w.WriteVector3Quantized(v);
            KudosSerializer<Vector3>.Read = r => r.ReadVector3Quantized();

            KudosSerializer<Quaternion>.Write = (w, v) => w.WriteQuaternionQuantized(v);
            KudosSerializer<Quaternion>.Read = r => r.ReadQuaternionQuantized();

            KudosSerializer<Color32>.Write = (w, v) => { w.WriteByte(v.r); w.WriteByte(v.g); w.WriteByte(v.b); w.WriteByte(v.a); };
            KudosSerializer<Color32>.Read = r => new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
        }
    }
}
