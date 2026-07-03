# Kudos Network Solution (KNS)

A bespoke Unity networking SDK for the Kudos VR social platform, combining the best ideas from **Photon Fusion**, **Mirror**, and **Coherence** into one code-first package built for peer-to-peer rooms of up to 32 players with integrated spatial VOIP.

- **Platforms:** Windows, macOS, Android (Meta Quest), WebGL — cross-play between all of them
- **Topology:** P2P listen-host (star). One player hosts; everyone else connects to them. No dedicated servers.
- **Transport:** WebRTC data channels everywhere (native via `com.unity.webrtc`, WebGL via a bundled `.jslib`), so one wire protocol works on every platform including the browser.
- **Backend:** a single tiny self-hosted Node.js service ("Nexus") providing WebSocket signaling + an atomic fill-or-create room registry.
- **Authority model:** distributed (shared) authority — every object is owned by exactly one peer, ownership transfers on grab, the host arbitrates. No server authority; the platform is slow-paced and social, so no anti-cheat is needed by design.

---

## Design lineage — what we took from each SDK

| Subsystem | Inspired by | What we borrowed |
|---|---|---|
| Transport abstraction (`ITransport`) | Mirror | Clean transport interface, connection-id model, pooled writers |
| Tick simulation & `NetworkFixedUpdate` | Photon Fusion | Fixed tick accumulator (20 Hz default), simulate-then-replicate loop |
| Delta snapshots with dirty bits | Photon Fusion | Per-object 64-bit dirty mask, eventual consistency, unreliable-sequenced state channel |
| Quantization | Photon Fusion | 24-bit fixed-point positions (1 mm), smallest-three quaternions (4 bytes) |
| `SyncVar<T>` fields | Mirror | Declarative synced fields with `OnChanged` hooks — but **no IL weaving** (see below) |
| Attribute RPCs (`[KudosRpc]`) | Mirror | Single attribute + `RpcTarget` enum, FNV-1a name-hash dispatch |
| Per-field bindings via reflection | Coherence | Fields discovered and bound at runtime, no codegen step |
| Authority transfer on grab | Coherence | `RequestAuthority()` / `ReleaseAuthority()` flow on `KudosGrabbable` |
| Rooms service (fill-or-create) | Coherence / Photon | `JoinOrCreateRoom(sceneKey)` — join an open room hosting that scene, else create one |
| Floating origin | Coherence | Optional `FloatingOrigin` utility for large worlds |
| Host migration | (our own, Fusion-shared-adjacent) | Deterministic election by join order; state survives because every peer already replicates everything |

**Why no IL weaving / codegen?** Mirror and Fusion both post-process assemblies. That is powerful but fragile across Unity versions and painful to debug. KNS instead uses `SyncVar<T>` wrapper fields discovered once per type via cached reflection (declaration order via `MetadataToken`). The cost is one wrapper object per field; the benefit is a plain C# package with zero build-pipeline magic. For a 32-player social platform this trade is comfortably worth it.

---

## Architecture at a glance

```
                      ┌─────────────────────────┐
                      │   Nexus (Node.js, ws)   │   self-hosted
                      │ signaling + room registry│
                      └───────────┬─────────────┘
              join-or-create /    │    \ offer/answer/ICE relay
                    ┌─────────────┼─────────────┐
                    │             │             │
                ┌───▼───┐     ┌───▼───┐     ┌───▼───┐
                │ Peer A │◄───►│ HOST  │◄───►│ Peer B │   WebRTC data channels
                └────────┘     └───┬───┘     └────────┘   (3 channels: Reliable,
                                   │                       StateSync, Voice)
                               ┌───▼───┐
                               │ Peer C │
                               └────────┘
```

- **Star topology:** clients only connect to the host. The host relays state deltas, RPCs, and voice frames (SFU-style, with optional distance culling for voice).
- **Signaling is only used to establish/repair connections.** Once WebRTC is up, Nexus only sees heartbeats.

### The three data channels

| Channel | Reliability | Carries |
|---|---|---|
| `Reliable` (0) | reliable, ordered | Welcome, spawns/despawns, RPCs, authority messages, migration |
| `StateSync` (1) | unreliable, sequenced | Tick delta snapshots (dropped-stale protection per sender) |
| `Voice` (2) | unreliable | Opus frames, 20 ms, seq-numbered for the jitter buffer |

### Tick loop

`KudosNetworkManager.Update()` accumulates time and steps a fixed tick (default **20 Hz**). Each tick: `NetworkFixedUpdate()` on all `KudosBehaviour`s → `ReplicationSystem.SendDeltas()` serializes dirty fields of locally-owned objects → host relays verbatim to everyone else. Remote transforms interpolate ~2 ticks behind for smoothness.

### Host migration (first-class)

1. Host vanishes → all clients detect the closed connection.
2. Deterministic election: **lowest surviving `JoinOrder` becomes host.** No votes, no coordination needed — everyone computes the same answer.
3. New host claims the room at Nexus (`claim-host`), bumps the **state epoch**, allocates a fresh PlayerId range.
4. Losers reconnect via signaling with jittered retry; a 10 s timeout falls back to a fresh `JoinOrCreateRoom(sceneKey)`.
5. Because authority is distributed and every peer replicates full state, **no world state is lost** — the new host simply continues.

### VOIP

- Mic capture at 48 kHz mono, 20 ms frames (960 samples).
- RMS voice-activity detection with 0.4 s hangover (silence costs zero bandwidth).
- Opus at ~16 kbps (interface provided; wire in [Concentus](https://github.com/lostromb/concentus) — pure C#, works on all four platforms including WebGL).
- Host relays frames SFU-style with optional **distance culling** (default 25 m) using avatar head positions.
- Per-speaker jitter buffer with Opus packet-loss concealment; playback is fully spatialized at the speaker's avatar head.

### Bandwidth reality check (32-player Quest host)

The host is the bottleneck. Rough worst case, everyone talking and moving:

- Voice: 31 inbound × 16 kbps = ~0.5 Mbps in; relaying each to 30 others = ~15 Mbps out **without culling**. With 25 m distance culling in a social space, real fan-out is typically 5–8 listeners per speaker → **2–4 Mbps out**.
- State: avatar (head + 2 hands) ≈ 40 bytes/tick × 20 Hz × 31 peers ≈ 0.25 Mbps in, ~7 Mbps out un-culled, again far less in practice with interest management.

A Quest 3 on decent Wi-Fi sustains this, but for 32-player rooms we recommend hosts on desktop when possible; the room registry can be extended to prefer desktop hosts. For smaller rooms (≤16) any device hosts comfortably.

**Interest management (below) is what turns the un-culled numbers into the culled ones.** With the default 20 m radius in a plaza-scale space, host state upload typically drops 3–5×.

---

## Built-in systems beyond the core

### Interest management (host-side AOI)

Two tiers, decided per recipient on the host each tick (`Runtime/Core/Simulation/InterestManagement.cs`):

- **Near** (within `InterestRadius` of the recipient's avatar head, default 20 m, with 1.2× exit hysteresis): full-rate 20 Hz deltas, exactly as before.
- **Far** (everything else): no deltas at all; instead a **full-state refresh** on the Reliable channel every `FarRefreshIntervalTicks` (default 20 ticks = 1 Hz). Far objects still exist and stay roughly current — they just stop costing per-tick bandwidth.

When an object crosses far→near, the host immediately sends a full-state catch-up so it doesn't interpolate from stale data. Objects with no position (or before avatars spawn) fail **open** — they replicate to everyone. Toggle with `EnableInterestManagement` on the manager; clients need no changes (the host recomposes per-recipient delta packets from the same serialized spans, so the wire format is unchanged).

### Moderation (`Kudos.Network.Moderation`)

Session-scoped, client-side-first primitives — the shape every social platform ends up needing:

- `KudosModeration.SetVoiceMuted(player, true)` — their voice frames are dropped at your ear (after the host relay, so muting locally never affects others).
- `KudosModeration.SetBlocked(player, true)` — mute **plus** their avatar is hidden locally.
- `KudosModeration.Kick(player, reason)` — host-only; sends a reliable Kick message, disconnects them, and deny-lists their signaling id for the session so they can't immediately rejoin.
- **Personal-space bubble**: add the `KudosModeration` component near your camera rig; avatars entering `PersonalSpaceRadius` (default 0.75 m) fade out locally and reappear at 1.3× the radius (hysteresis prevents flicker).

Mute/block sets are per-session; persisting them against account ids is a marked TODO.

### Reconnect grace (Wi-Fi hiccup insurance)

Headsets drop Wi-Fi constantly. When a client's connection dies, the host now marks them **away** for `ReconnectGraceSeconds` (default 15 s) instead of removing them: their avatar freezes in place, scene props they held return to host authority, and everyone gets an `OnPlayerAway` event. If they reconnect in time (the client automatically rebuilds its transport and sends a `rejoin-room` to Nexus, then presents a one-time resume token), they get their **same PlayerId** back, a fresh full-state sync, and an `OnPlayerResumed` event — no despawn/respawn churn. Miss the window and they're cleanly evicted.

### Diagnostics: stats overlay + live inspector

- **`NetworkStatsOverlay`** (`Kudos.Network.Utils`): drop on any GameObject, press **F3**. Shows role, room, tick, object count, per-peer RTT and away flags, and live in/out kbps + packets/s **per channel** (Reliable / StateSync / Voice), counted at the transport so nothing escapes measurement.
- **KudosObject inspector** (editor): select any networked object in play mode and see its NetworkId, authority, and a live table of every SyncVar's value and dirty state. "Why isn't my door syncing" becomes a ten-second glance.

### Network parenting (held objects that don't lag)

`KudosTransform` can replicate **local to an avatar node** instead of world space: `SetNetworkParent(player, AvatarNode.RightHand)` / `ClearNetworkParent()`. While parented, remotes compose the pose through the parent's hand every frame, so a held prop stays glued to a moving avatar instead of trailing it by an interpolation delay — and it costs fewer bytes, since local offsets barely change. `KudosGrabbable` uses this automatically: grab → authority transfer → parent to the grabbing hand (`GrabNode`, or pass the hand explicitly via `Grab(AvatarNode)`); release → unparent → authority back to host.

---

## Quick start

1. **Install the package** — in Unity: *Window → Package Manager → + → Add package from git URL*:
   ```
   https://github.com/YOUR-ORG/kudos-network.git#v0.1.0
   ```
   (Requires Git on your machine. Pin a tag as shown; omit `#v0.1.0` to track the default branch. Alternatively clone the repo and *Add package from disk*, or copy it into `Packages/com.kudos.network/` to hack on it directly.)
2. **Import the examples** (optional): select the package in Package Manager → *Samples* tab → **Import** next to "KNS Examples". They land in `Assets/Samples/` as editable copies.
3. **Run the backend** (any box with Node 18+ — it lives in `Backend~/` inside the repo, which Unity deliberately ignores):
   ```bash
   cd Backend~
   npm install
   node server.js        # listens on ws://0.0.0.0:8787
   ```
4. **Scene setup**: add an empty GameObject with `KudosNetworkManager`. Set `NexusUrl` to your server, assign your **Avatar Prefab** (must have `KudosObject` + `KudosAvatar`), and register any other spawnable prefabs.
5. **Join**:
   ```csharp
   KudosNetworkManager.Instance.JoinOrCreateRoom("plaza", displayName: "Robyn");
   ```
   The Nexus registry atomically fills an existing `plaza` room with space, or makes you the host of a new one.
6. **Make things networked** (note the three usings — `SyncVar<T>` lives in `.State`, `[KudosRpc]` in `.Rpc`):
   ```csharp
   using Kudos.Network;
   using Kudos.Network.Rpc;
   using Kudos.Network.State;

   public class ScoreBoard : KudosBehaviour
   {
       public SyncVar<int> Score = new SyncVar<int>();      // replicates automatically

       [KudosRpc(RpcTarget.All)]
       public void Celebrate(string who) { /* runs on everyone */ }

       public override void NetworkFixedUpdate()
       {
           if (Object.HasAuthority) Score.Value += 1;        // only the owner writes
       }
   }
   ```
7. **Grabbables**: put `KudosObject` + `KudosGrabbable` on a prop. Call `Grab()` from your interaction system — authority transfers to the grabber; `Release()` hands it back.

---

## Integration checklist (skeleton → production)

This is a **code-first skeleton**: the architecture, protocols, serialization, and systems are complete and internally consistent, but third-party native dependencies are stubbed with clearly marked `TODO(integration)` points.

1. **WebRTC (native platforms):** install `com.unity.webrtc` and fill in the marked TODOs in `UnityRtcPeerConnection` (`Runtime/Transport/WebRtcTransport.cs`). The WebGL path (`Plugins/WebGL/KudosWebRtc.jslib`) is already complete.
2. **TURN server:** deploy [coturn](https://github.com/coturn/coturn) next to Nexus and add its URL/credentials to `RtcConfig`. STUN alone fails for ~10–15 % of home NATs.
3. **Opus:** add Concentus and implement `IVoiceCodec` in `VoiceCodecFactory` (the passthrough stub works for LAN testing but is 24× the bandwidth).
4. **Prefab hashes:** ensure every networked prefab's `PrefabHash` is baked/unique (an editor utility is a good follow-up).
5. **Harden Nexus:** put it behind TLS (`wss://`), add an allow-list or token auth for `hello`.

## Known skeleton gaps (deliberate, documented in code)

- **Authority-race shadowing:** during an authority transfer, a couple of stale deltas from the old owner may apply before the grant lands. Fix: brief per-object shadow window after `AuthorityGranted`. (`ReplicationSystem.cs`)
- **Deltas for unknown objects are dropped**, not buffered. Under heavy join churn a joiner can miss one tick of state for an object spawned in the same instant. Eventual consistency covers it; a small buffer is the clean fix. (`ReplicationSystem.cs`)
- **Client-initiated spawn** goes through an RPC to the host in this skeleton; a nicer API would return a task that completes with the spawned object.
- **RTT stats on WebGL** are not surfaced (native path has Ping/Pong; the browser `getStats()` hook is a TODO in the jslib).
- **Kick vs. disconnect race:** the Kick message and the forced disconnect are sent back-to-back; if the disconnect wins, the kicked client sees a generic disconnect instead of the kick reason. Self-healing (they're out either way), but a small delay before `Disconnect` would tidy it.
- **Mute/block lists are session-scoped** (keyed by PlayerId). Persistence belongs against your account system — marked TODO in `KudosModeration`.
- **Far-tier refreshes are per-object reliable messages**; under thousands of scene objects you'd want to batch them. Fine at social-space object counts.

## Package layout

```
Runtime/
  Core/            KudosNetworkManager, KudosObject, KudosBehaviour, types
    Simulation/    ReplicationSystem (tick deltas), InterestManagement (AOI)
    Serialization/ KudosWriter/Reader (pooled), Quantization
    State/         SyncVar<T> + serializer registry
    Rpc/           [KudosRpc] system
  Transport/       ITransport, SignalingClient, WebRtcTransport, WebGL bridge
  Rooms/           RoomManager (fill-or-create + rejoin), HostMigration
  Voip/            KudosVoice, MicrophoneCapture, JitterBuffer, SpatialVoicePlayer, codec interface
  Moderation/      KudosModeration (mute/block/kick, personal-space bubble)
  Components/      KudosTransform (+ network parenting), KudosAvatar, KudosGrabbable
  Utils/           FloatingOrigin, NetworkStats, NetworkStatsOverlay
Editor/            KudosObjectInspector (live SyncVar view)
Plugins/WebGL/     KudosWebRtc.jslib (browser WebRTC + WebSocket)
Samples~/Examples/ importable examples (Package Manager -> Samples tab)
Backend~/          Nexus server (Node.js) - the ~ suffix hides it from Unity import
```

Built for Kudos. Take what works, question what doesn't, and ship something people love hanging out in.
