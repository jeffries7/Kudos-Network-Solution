// Kudos Network Solution (KNS)
// RPC system.
//
// DESIGN LINEAGE (Mirror): attribute-marked methods, invoked by name. Mirror's
// [Command]/[ClientRpc] split exists because of server authority; KNS has
// distributed authority, so a single [KudosRpc] attribute plus an explicit
// target (All / Others / One / Host) is simpler and covers every case.
//
// Dispatch is by stable FNV-1a hash of "TypeName.MethodName" + reflection
// (cached MethodInfo). No IL weaving. Reflection invoke costs ~microseconds -
// irrelevant for RPCs, which are events (emote played, mute toggled, door
// opened), not per-tick traffic. Per-tick traffic belongs in SyncVars.

using System;
using System.Collections.Generic;
using System.Reflection;
using Kudos.Network.Serialization;
using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network.Rpc
{
    /// <summary>Mark a method on a KudosBehaviour as remotely invokable.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class KudosRpcAttribute : Attribute { }

    public enum RpcTarget : byte { All = 0, Others = 1, One = 2, Host = 3 }

    public sealed class RpcSystem
    {
        private readonly KudosNetworkManager _manager;

        // methodHash -> cached invoker
        private static readonly Dictionary<uint, CachedRpc> Registry = new Dictionary<uint, CachedRpc>();
        private static readonly HashSet<Type> ScannedTypes = new HashSet<Type>();

        private sealed class CachedRpc
        {
            public MethodInfo Method;
            public Type[] ParamTypes;
        }

        public RpcSystem(KudosNetworkManager manager) => _manager = manager;

        // ------------------------------------------------------------------ registration

        internal static void EnsureScanned(Type behaviourType)
        {
            if (!ScannedTypes.Add(behaviourType)) return;
            foreach (var method in behaviourType.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<KudosRpcAttribute>() == null) continue;
                uint hash = Fnv1a($"{behaviourType.Name}.{method.Name}");
                if (Registry.TryGetValue(hash, out var existing) && existing.Method != method)
                {
                    Debug.LogError($"[KNS] RPC hash collision: {behaviourType.Name}.{method.Name} vs {existing.Method.DeclaringType.Name}.{existing.Method.Name}. Rename one.");
                    continue;
                }
                var parameters = method.GetParameters();
                var types = new Type[parameters.Length];
                for (int i = 0; i < parameters.Length; i++) types[i] = parameters[i].ParameterType;
                Registry[hash] = new CachedRpc { Method = method, ParamTypes = types };
            }
        }

        public static uint Fnv1a(string s)
        {
            uint hash = 2166136261u;
            foreach (char c in s) { hash ^= c; hash *= 16777619u; }
            return hash;
        }

        // ------------------------------------------------------------------ sending

        internal void Send(KudosBehaviour behaviour, string methodName, RpcTarget target, PlayerId targetPlayer, object[] args)
        {
            EnsureScanned(behaviour.GetType());
            uint hash = Fnv1a($"{behaviour.GetType().Name}.{methodName}");
            if (!Registry.TryGetValue(hash, out var rpc))
            {
                Debug.LogError($"[KNS] No [KudosRpc] method '{methodName}' on {behaviour.GetType().Name}");
                return;
            }
            if (rpc.ParamTypes.Length != args.Length)
            {
                Debug.LogError($"[KNS] RPC '{methodName}' expects {rpc.ParamTypes.Length} args, got {args.Length}");
                return;
            }

            var writer = KudosWriter.Rent();
            writer.WriteByte((byte)MsgId.Rpc);
            writer.WriteVarUInt(behaviour.NetworkId.Value);
            // Behaviour index disambiguates two components of the same type on one object.
            writer.WriteByte(BehaviourIndex(behaviour));
            writer.WriteUInt(hash);
            writer.WriteByte((byte)target);
            writer.WriteUShort(targetPlayer.Value);
            writer.WriteByte((byte)args.Length);
            for (int i = 0; i < args.Length; i++)
                WriteArg(writer, rpc.ParamTypes[i], args[i]);

            _manager.RouteRpc(writer.ToSegment(), target, targetPlayer, invokeLocally: target == RpcTarget.All);
            KudosWriter.Return(writer);
        }

        private static byte BehaviourIndex(KudosBehaviour behaviour)
        {
            var list = behaviour.Object.Behaviours;
            for (byte i = 0; i < list.Length; i++)
                if (ReferenceEquals(list[i], behaviour)) return i;
            return 0;
        }

        // ------------------------------------------------------------------ receiving

        internal void HandleIncoming(KudosReader reader)
        {
            uint netId = reader.ReadVarUInt();
            byte behaviourIndex = reader.ReadByte();
            uint hash = reader.ReadUInt();
            reader.ReadByte();                    // target (already routed by manager)
            reader.ReadUShort();                  // targetPlayer
            byte argCount = reader.ReadByte();

            if (!_manager.TryGetObject(new NetworkId(netId), out var obj)) return;
            if (!Registry.TryGetValue(hash, out var rpc))
            {
                // Types are lazily scanned; the receiver may not have scanned yet.
                foreach (var b in obj.Behaviours) EnsureScanned(b.GetType());
                if (!Registry.TryGetValue(hash, out rpc)) return;
            }

            var args = new object[argCount];
            for (int i = 0; i < argCount; i++)
                args[i] = ReadArg(reader, rpc.ParamTypes[i]);

            if (behaviourIndex < obj.Behaviours.Length)
                rpc.Method.Invoke(obj.Behaviours[behaviourIndex], args);
        }

        internal void InvokeLocal(ArraySegment<byte> packet)
        {
            var reader = new KudosReader(packet);
            reader.ReadByte(); // MsgId
            HandleIncoming(reader);
        }

        // ------------------------------------------------------------------ arg (de)serialization

        private static void WriteArg(KudosWriter w, Type type, object value)
        {
            if (type == typeof(int)) w.WriteInt((int)value);
            else if (type == typeof(float)) w.WriteFloat((float)value);
            else if (type == typeof(bool)) w.WriteBool((bool)value);
            else if (type == typeof(string)) w.WriteString((string)value);
            else if (type == typeof(byte)) w.WriteByte((byte)value);
            else if (type == typeof(ushort)) w.WriteUShort((ushort)value);
            else if (type == typeof(Vector3)) w.WriteVector3((Vector3)value);
            else if (type == typeof(Quaternion)) w.WriteQuaternionQuantized((Quaternion)value);
            else if (type == typeof(PlayerId)) w.WriteUShort(((PlayerId)value).Value);
            else if (type == typeof(byte[])) { var b = (byte[])value; w.WriteSegment(new ArraySegment<byte>(b)); }
            else Debug.LogError($"[KNS] Unsupported RPC arg type {type.Name}. Supported: int, float, bool, string, byte, ushort, Vector3, Quaternion, PlayerId, byte[].");
        }

        private static object ReadArg(KudosReader r, Type type)
        {
            if (type == typeof(int)) return r.ReadInt();
            if (type == typeof(float)) return r.ReadFloat();
            if (type == typeof(bool)) return r.ReadBool();
            if (type == typeof(string)) return r.ReadString();
            if (type == typeof(byte)) return r.ReadByte();
            if (type == typeof(ushort)) return r.ReadUShort();
            if (type == typeof(Vector3)) return r.ReadVector3();
            if (type == typeof(Quaternion)) return r.ReadQuaternionQuantized();
            if (type == typeof(PlayerId)) return new PlayerId(r.ReadUShort());
            if (type == typeof(byte[])) { var seg = r.ReadSegment(); var arr = new byte[seg.Count]; Buffer.BlockCopy(seg.Array, seg.Offset, arr, 0, seg.Count); return arr; }
            return null;
        }
    }
}
