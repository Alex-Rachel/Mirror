# Mirror Contracts

Use these as conversion invariants, then verify them against the installed version. Official documentation is guidance; callback names, available components, generated serialization, and synchronization settings can change. The source ledger below and [examples-patterns.md](examples-patterns.md) use paths relative to `Assets/Mirror/`, checked on **2026-10-15** at reference commit **`3c7efe2d0763cacb04a2d5db36e10103eac22af3`**. Refresh relevant official pages and target source during each conversion; the reference revision does not identify the target dependency.

## Authority and Requests

Default to server-resolved shared gameplay. Owned clients submit bounded intentions, not final health, reward, inventory, or victory results. On the server validate the actual connection/player, target existence, ownership/permissions, finite numeric values, range/line of sight as applicable, cooldown/sequence, and per-connection request budget. Reject invalid requests without partial mutation.

`[Command]` normally requires authority over its object. Use an owned player request entry point where suitable. With `requiresAuthority=false`, explicitly validate Mirror's injected `NetworkConnectionToClient sender = null`; never trust a supplied player ID or object reference as authentication. The sample documentation omits game-specific validations.

Keep four concepts separate (C1): server adjudication decides game outcomes; ownership associates an identity with a client; `syncDirection` selects a component's serialization writer; `SyncMode.Owner` selects owner-only state recipients. Neither a direction nor a recipient setting validates requests. `isLocalPlayer` identifies the primary local player; `isOwned` also applies to other owned objects. Per-component `authority` depends on side, direction and ownership: in this reference Host returns `ServerToClient || isOwned`, not universally true. On the server use the received connection and authoritative state. `[Server]` and `[Client]` guard execution side; they are not authorization policies.

Remote methods must match the installed Weaver contract (C2): instance, void, nongeneric, noncoroutine, supported parameters, no ref/out or ordinary optional parameters. A Command's injected sender is the special optional connection, not serialized client identity. A TargetRpc's first connection parameter, when present, selects its recipient. Keep business validation for Commands **and** custom NetworkMessage handlers, including phase/membership and retry/sequence rules.

## State, Events, and Initialization

Store recoverable state in server-controlled SyncVars/SyncObjects or a deliberately versioned snapshot mechanism. Use ClientRpc for observer-facing transient effects, TargetRpc for intended recipients, and Commands for client requests. RPC history does not restore a late joiner's health, equipment, door state, score, round, or inventory.

Treat initial full state and incremental changes separately (C3). Initial SyncVar state is available in `OnStartClient`; render it explicitly and keep hooks/presentation idempotent. The reference implementation invokes hooks through generated initial deserialization, including deferred initial-spawn hooks; do not repeat the nearby stale comment that hooks are delta-only. A hook may still not fire for an unchanged default. Verify declaration order, cross-field dependencies, callback signatures, Host behavior and default-valued initial state in the installed source. SyncLists must be initialized and readonly; subscription after initial population requires rendering existing contents, not expecting historic change callbacks. Unsubscribe on teardown.

For custom `OnSerialize/OnDeserialize`, define the base-data policy and a matching layout for both `initialState=true` and `false` (C4). Base methods here include **both SyncObjects and SyncVars**. Preserve them with matching base calls when extending; deliberately replace them only if the whole required state is covered and tested. Match field order, types, conditions, masks and collection bounds in both directions. Bound/validate client-originated data before committing it. Round-trip initial and delta data, including base fields and zero/default values; prove remote late join as well as updates.

Use `SyncMode.Owner` or equivalent where appropriate for private data. Treat network references as potentially missing due to spawn order, interest management, or despawn. Use stable IDs, bounded resolution, and disappearance handling. Supported spawned `Transform`/`NetworkBehaviour` references carry network identity/component IDs, not whole Unity objects; arbitrary components require an explicit supported serializer. Concrete serializable `ScriptableObject` subclasses can transfer supported field data into new instances, not preserve asset identity; stable catalog IDs often fit shared game content better. Verify the installed serializers, collection bounds, and cross-assembly SyncVar access with Weaver.

## Lifecycle, Scenes, and Movement

When using NetworkManager, keep at most one active instance on an object without NetworkIdentity. Preserve a working custom network bootstrap when the project uses one; adding a manager is not mandatory. Do not nest NetworkIdentity components. NetworkBehaviours under one identity must have matching layout on peers. Spawn dynamic network objects on the server and register their client prefabs/handlers before use.

Keep ownership transfer distinct from primary-player replacement (C5). `AssignClientAuthority` requires a spawned server identity and removal of a different previous owner first. A primary player's authority cannot be removed directly; use the installed player removal/replacement API and deliberate keep/unspawn/destroy options. `Spawn(obj, conn)` adds an owned object, not another primary player. Rebind input/UI on `OnStartAuthority/OnStopAuthority` and local-player changes, clear old controls/subscriptions, and validate the **current** owner for any bypass-authority entry point. Revocation cannot erase information already delivered.

Use network-aware Destroy/UnSpawn and an explicit entity-lifecycle policy (C6). Same-entity unspawn/respawn can retain gameplay state; `NetworkIdentity.ResetState` deliberately retains SyncObjects here. Pool reuse for a new entity must reset its domain data, ownership, subscriptions, timers, availability, velocities, visuals and relevant interpolation/prediction buffers **before spawn**. Do not blindly clear continuing-player progress or assume Mirror resets it. Host shares server objects, so its spawn/unspawn path differs from remote clients. In this reference, `NetworkServer.UnSpawnInternal` **does invoke** the client unspawn handler on Host; do not infer both handlers are skipped from older documentation or add a second return. Trace the installed version and implement exactly-once return for Host, server-only and remote clients. Cancel delayed work, unregister handlers and tear down events on stop; test shutdown while respawn is pending.

Keep player association correct when overriding manager callbacks, preserving base behavior or its required equivalent. Use `OnRoom*` extension points on NetworkRoomManager and transfer approved state before gameplay replacement. Gate requests until session/scene/player readiness. Server-coordinated transitions must validate actual load completion; fixed delays do not establish it. Late joiners must enter the active scene/round. Additive instances and matches require separate membership, observer, message/interaction and physics rules (C7). Use actual Scene handles for repeated scene names; move spawned objects into the correct instance, simulate local physics scenes and perform queries in them. Interest management is visibility filtering, not business authorization or physical isolation.

Select one motion/physics writer per target (C8). Remote copies must not run competing input, controllers, AI or rigidbody simulation. Inspect component inheritance and **saved** `target`, direction, interval and rigidbody settings before adding sync: this revision's NetworkRigidbodyReliable already inherits NetworkTransformReliable, and NetworkTransformBase.Reset chooses ClientToServer. Match the writer to that configuration and validate client-driven motion where trust matters. Test transfer/revocation as well as stop/start, including kinematic state and stale buffers. Choose Prediction, Hybrid, reconciliation or lag compensation for measured gameplay needs; benchmark tuning and snapshot/send-rate settings are not universal defaults.

Use NetworkAnimator where appropriate; network animation triggers require its network-aware trigger path. Keep animation/audio/VFX presentation separate from authoritative damage and rewards.

## Source Ledger

All rows inherit the date/commit at the top. Paths and symbols describe observed mechanisms; validation and acceptance are production requirements derived from their limits. Resolve them in the target source before implementation.

| Rule | Source path and key symbols | Observable assertions in `acceptance.md` |
| --- | --- | --- |
| C1: ownership, writer, recipients | `Core/NetworkBehaviour.cs::authority/syncDirection/syncMode`; `Core/NetworkIdentity.cs::SerializeServer_Spawn/SerializeServer_Broadcast/SerializeClient`; `Examples/SyncDirection/Player.cs` | M3, M7, M9 |
| C2: remote contract and sender | `Editor/Weaver/Processors/NetworkBehaviourProcessor.cs::ValidateRemoteCallAndParameters/ValidateParameter/IsSenderConnection`; `Editor/Weaver/Processors/CommandProcessor.cs::AddSenderConnection`; `Editor/Weaver/Processors/TargetRpcProcessor.cs::HasNetworkConnectionParameter`; `Core/NetworkServer.cs::OnCommandMessage` | M1, M3, M4; compile/Weaver gate |
| C3: initial and incremental state | `Core/NetworkBehaviour.cs::GeneratedSyncVarDeserialize/deferredSyncVarHooks`; `Core/NetworkClient.cs::OnObjectSpawnFinished`; `Core/SyncList.cs::OnDeserializeAll/OnDeserializeDelta`; `Editor/Weaver/Processors/NetworkBehaviourProcessor.cs::GenerateDeSerialization` | M2, M8 |
| C4: custom serialization | `Core/NetworkBehaviour.cs::OnSerialize/OnDeserialize/SerializeSyncObjects/DeserializeSyncObjects`; Weaver `GenerateSerialization/GenerateDeSerialization` | M2 plus initial/delta round trips including base state |
| C5: association and transfer | `Core/NetworkIdentity.cs::AssignClientAuthority/RemoveClientAuthority`; `Core/NetworkServer.cs::Spawn/ReplacePlayerForConnection`; `Components/NetworkRoomManager.cs::SceneLoadedForPlayer`; `Examples/CouchCoop/Scripts/CouchPlayerManager.cs::CmdAddPlayer` | M3, M5, M7 |
| C6: respawn and reuse | `Core/NetworkIdentity.cs::ResetState`; `Core/NetworkClient.cs::InvokeUnSpawnHandler`; `Core/NetworkServer.cs::UnSpawnInternal/UnSpawn`; `Examples/Room/Scripts/Spawner.cs::RecycleReward/ClearPool` | M1, M2, M6, M8 |
| C7: instance and match isolation | `Components/InterestManagement/Scene/SceneInterestManagement.cs::OnCheckObserver`; `Components/InterestManagement/Match/MatchInterestManagement.cs::OnCheckObserver`; `Components/InterestManagement/PhysicsSimulator/PhysicsSimulator.cs::FixedUpdate`; additive/match source in the selection reference | M4, M5, M8 |
| C8: unique motion writer | `Components/NetworkRigidbody/NetworkRigidbodyReliable.cs::FixedUpdate/OnStopServer/OnStopClient`; `Components/NetworkTransform/NetworkTransformBase.cs::Reset/OnClientAuthorityChanged`; `Components/NetworkAnimator.cs::SetTrigger/ResetTrigger` | M3, M8, M9 |

## Host, Transport, and Delivery

Host mode shares server/client objects. Server and client callbacks can both execute, and local RPC execution can duplicate effects if application code also applies them directly. Test exactly-once damage, purchases, pickup, spawn, and round initialization on host and remote peers.

Choose transport by platform and service requirements. KCP uses UDP; browser clients need an appropriate WebSocket transport, with Multiplex when necessary. LAN discovery is UDP broadcast, not internet matchmaking. Authentication and transport confidentiality are separate concerns. Protect credentials using appropriate encrypted transport/HTTPS; do not imply that choosing Mirror or assigning ownership provides these protections.

Use development-only diagnostic hooks for tests and keep automation/eval servers out of release builds. Check dedicated/server-only code without rendering or a local player. Measure message counts/bytes, allocations and frame cost for the agreed player/object load before making performance claims.

## Official Reading by Topic

| Topic | Official source |
| --- | --- |
| Overview, host behavior | https://mirror-networking.gitbook.io/docs/manual/general |
| Component selection | https://mirror-networking.gitbook.io/docs/manual/components |
| Manager and scene wiring | https://mirror-networking.gitbook.io/docs/manual/components/network-manager |
| Identity and spawning | https://mirror-networking.gitbook.io/docs/manual/components/network-identity |
| Ownership | https://mirror-networking.gitbook.io/docs/manual/guides/authority |
| Commands and RPC | https://mirror-networking.gitbook.io/docs/manual/guides/communications/remote-actions |
| State and late join | https://mirror-networking.gitbook.io/docs/manual/guides/synchronization/syncvars |
| Collection initialization | https://mirror-networking.gitbook.io/docs/manual/guides/synchronization/synclists |
| Serialization and references | https://mirror-networking.gitbook.io/docs/manual/guides/serialization |
| Supported data types | https://mirror-networking.gitbook.io/docs/manual/guides/data-types |
| Pools and host differences | https://mirror-networking.gitbook.io/docs/manual/guides/gameobjects/custom-spawnfunctions |
| Movement | https://mirror-networking.gitbook.io/docs/manual/components/network-transform |
| Transports | https://mirror-networking.gitbook.io/docs/manual/transports |

When a page is unavailable to the browsing tool, append `.md`. Do not embed a copied manual in the project.
