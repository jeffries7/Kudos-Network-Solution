// =====================================================================
//  KNS EXAMPLE 3 — Text chat (RPCs with arguments)
// ---------------------------------------------------------------------
//  Fire-and-forget events that aren't persistent state belong in RPCs,
//  not SyncVars. Chat is the classic case: late joiners don't need old
//  messages, so nothing is stored — the message is just broadcast.
//
//  Setup: put this + a KudosObject on one scene object (e.g. "Chat").
//
//  Demonstrates:
//    • [KudosRpc] methods with string/ushort arguments
//    • RpcAll (runs on every peer, including the sender)
//    • Looking up peer info from Runner.Peers
// =====================================================================

using System;
using System.Collections.Generic;
using Kudos.Network;
using Kudos.Network.Rpc;
using Kudos.Network.State;
using UnityEngine;

namespace Kudos.Network.Examples
{
    public class NetworkChat : KudosBehaviour
    {
        [Tooltip("How many messages to keep in the local scrollback.")]
        public int MaxHistory = 50;

        /// <summary>Local scrollback — bind your UI to this (or to OnMessage).</summary>
        public readonly List<string> History = new List<string>();

        /// <summary>Raised on every peer whenever any message arrives (sender name, text).</summary>
        public event Action<string, string> OnMessage;

        /// <summary>Call from your input field's submit handler.</summary>
        public void Send(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (text.Length > 200) text = text.Substring(0, 200);   // be kind to the reliable channel

            // Send our PlayerId rather than our name: ids are tiny and the
            // name can be resolved (and is authoritative) on the receiver.
            RpcAll(nameof(ReceiveMessage), Runner.LocalPlayerId.Value, text);
        }

        [KudosRpc]
        public void ReceiveMessage(ushort senderRaw, string text)
        {
            var senderId = new PlayerId(senderRaw);
            string name = Runner.Peers.TryGetValue(senderId, out var peer)
                ? peer.DisplayName
                : $"Player {senderRaw}";

            string line = $"{name}: {text}";
            History.Add(line);
            if (History.Count > MaxHistory) History.RemoveAt(0);

            Debug.Log($"[Chat] {line}");
            OnMessage?.Invoke(name, text);
        }
    }
}
