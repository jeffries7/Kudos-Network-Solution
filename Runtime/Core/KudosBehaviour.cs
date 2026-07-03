// Kudos Network Solution (KNS)
// KudosBehaviour - base class for all networked scripts.
// (Fusion: NetworkBehaviour + FixedUpdateNetwork | Mirror: NetworkBehaviour)

using Kudos.Network.Rpc;
using UnityEngine;

namespace Kudos.Network
{
    [RequireComponent(typeof(KudosObject))]
    public abstract class KudosBehaviour : MonoBehaviour
    {
        /// <summary>The identity this behaviour replicates through. Set during Bind().</summary>
        public KudosObject Object { get; internal set; }

        public bool HasAuthority => Object != null && Object.HasAuthority;
        public NetworkId NetworkId => Object != null ? Object.NetworkId : NetworkId.None;
        public KudosNetworkManager Runner => KudosNetworkManager.Instance;

        // ------------------------------------------------------------------ lifecycle hooks

        /// <summary>Object is live on the network; SyncVars hold their initial replicated values.</summary>
        public virtual void OnSpawned() { }

        public virtual void OnDespawned() { }

        /// <summary>This peer just became the authority (spawned it, grabbed it, or inherited via host migration).</summary>
        public virtual void OnGainedAuthority() { }

        public virtual void OnLostAuthority() { }

        /// <summary>
        /// Fixed-rate network tick (Fusion's FixedUpdateNetwork). Runs at
        /// KudosNetworkManager.TickRate on EVERY peer, but you should only
        /// mutate state when HasAuthority. Visual smoothing belongs in Update().
        /// </summary>
        public virtual void NetworkFixedUpdate(float deltaTime) { }

        // ------------------------------------------------------------------ RPC send helpers

        /// <summary>Invoke [KudosRpc] method on all peers (including, optionally, the caller).</summary>
        protected void RpcAll(string methodName, params object[] args)
            => Runner.Rpc.Send(this, methodName, RpcTarget.All, PlayerId.None, args);

        /// <summary>Invoke on every peer except the caller.</summary>
        protected void RpcOthers(string methodName, params object[] args)
            => Runner.Rpc.Send(this, methodName, RpcTarget.Others, PlayerId.None, args);

        /// <summary>Invoke on one specific peer.</summary>
        protected void RpcTo(PlayerId target, string methodName, params object[] args)
            => Runner.Rpc.Send(this, methodName, RpcTarget.One, target, args);

        /// <summary>Invoke on the current host only (survives host migration).</summary>
        protected void RpcHost(string methodName, params object[] args)
            => Runner.Rpc.Send(this, methodName, RpcTarget.Host, PlayerId.None, args);
    }
}
