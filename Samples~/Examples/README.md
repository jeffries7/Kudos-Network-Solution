# KNS Examples

Seven small, heavily commented scripts covering the SDK's core patterns. Each is self-contained; read them in order and you've covered the whole API surface.

| # | Script | Feature | Key concepts |
|---|---|---|---|
| 1 | `ConnectionBootstrap.cs` | Joining a room | `JoinOrCreateRoom`, lifecycle events, `IsHost`, `Peers` |
| 2 | `SyncedDoor.cs` | Shared world object | `SyncVar<bool>`, `OnChanged`, request-via-`RpcHost` pattern |
| 3 | `NetworkChat.cs` | Text chat | `[KudosRpc]` with args, `RpcAll`, peer lookup |
| 4 | `GrabbableDemo.cs` | Picking things up | Authority transfer, `OnGained/LostAuthority`, move-only-if-owner |
| 5 | `PlayerNameplate.cs` | Names over heads | Reading another component's SyncVar, local-vs-remote branching |
| 6 | `PropSpawner.cs` | Spawning objects | `Runner.Spawn/Despawn`, spawn-with-authority, prefab registry |
| 7 | `VoiceMuteToggle.cs` | Voice mute | `KudosVoice.MicEnabled` + replicated muted icon |

## The three patterns everything reduces to

1. **Persistent state → `SyncVar<T>`.** If a late joiner needs to see it (door open, score, muted icon), it's a SyncVar. Write it only on the authority; render from `OnChanged` or by reading `.Value`.
2. **Transient events → `[KudosRpc]`.** If late joiners don't care (chat line, sound cue, confetti), it's an RPC.
3. **Non-owners request, owners mutate.** Clients never write state they don't own — they `RpcHost(...)` (scene objects) or `RequestAuthority()` (grabbables), and the owner performs the change. This is what makes host migration and late-join "just work".

## Trying it locally

Two editor instances (or editor + build) on one machine, Nexus running on `ws://localhost:8787`:

1. Scene with `KudosNetworkManager` (avatar prefab assigned, prop prefab registered) + `ConnectionBootstrap`.
2. Add a door (cube + `KudosObject` + `SyncedDoor`), a prop (cube + Rigidbody + `KudosObject` + `KudosTransform` + `KudosGrabbable` + `GrabbableDemo`), and a spawner.
3. Run both instances — the first becomes host, the second fills the room. Click the door in either instance and watch it swing in both; click-grab the prop in one and drag it around in the other's view.
