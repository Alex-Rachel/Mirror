# Multiplayer Acceptance

Turn relevant rows into actual assertions against the target game. Do not create tests that only inspect text, call the same local setter from both sides, or replace Mirror with mocks for the remote path.

| Gate | Observable proof |
| --- | --- |
| Import/compile/Weaver | Unity completes compilation after final edits; no new compile/serialization/Weaver errors |
| Assets/scenes | Saved actual build scenes/prefabs load; valid identity hierarchy, manager or custom bootstrap, player/transport assignments, spawn registrations and script references |
| Connect/spawn | Host and independent client see the agreed players once, correctly associated with each connection |
| Input ownership | A client's input moves/acts only for its player; local camera/audio/UI follow it; remote observers update |
| Core gameplay | Movement plus the actual combat, interaction, AI, item/economy or round loop succeeds across peers |
| Validation | Unauthorized targets, excessive/out-of-range/nonfinite requests, invalid timing and duplicate rewards are rejected without state mutation |
| Consistency | Authoritative results converge on peers after a bounded wait; host does not double-apply outcomes |
| Late join | A client joining after state changes reconstructs current state; destroyed objects stay absent |
| Lifecycle | Disconnect removes/releases the right objects; rejected/full/failed joins recover; repeated sessions have no duplicate subscriptions, managers or stale state |
| Transitions | Ready/room/scene/round transitions and join during a transition preserve membership/state |
| Server-only | If supported, server + independent clients works without local player, camera or rendering |
| Network conditions | Chosen transport under documented latency/jitter/loss still meets agreed behavior/time bounds |
| Build/regression | Relevant target builds and existing game regression tests pass; new multiplayer tests execute |

Apply only relevant gameplay features, but never waive connect, ownership, core gameplay, consistency, and lifecycle for a claimed conversion. A missing required PlayMode suite should be added, not counted as a zero-test pass. Functional testing does not by itself establish production security or scale.

## Example-Derived Assertions

Map the chosen examples and C1-C8 contract rules to executable assertions. These are scenario IDs for the target harness, not fabricated discovered test names. Use the game's actual assets and quantities; customize features and bounds, while preserving the invariant.

| ID | Action and observation | Required invariant |
| --- | --- | --- |
| M1: contention and validation | Two independent clients request the same pickup in the same window; repeat/retry the request and try null, wrong-type, foreign-instance and out-of-range targets. Inspect server inventory, item availability and both clients. | Exactly one grant and one consumption; total inventory/resource quantity conserved. Rejections cause no reward, unrelated destruction or partial mutation. Host paths obey the same rule. |
| M2: state reconstruction | Equip/change usage or inventory, then join a fresh client and leave/re-enter visibility. Include zero/default state, initial collection population, and repeated presentation initialization; compare server state and rendered child/item/UI counts. | Current equipment/configuration and collection contents recover without RPC history; one correct presentation, no duplicate child/subscription; consumed objects remain absent. Custom serializers round-trip both initial and delta data including inherited SyncVars/SyncObjects. |
| M3: ownership and replacement | Transfer a controllable object from A to B; send normal and bypass-authority requests from A afterward. Replace the primary player and exercise multiple owned objects where supported. | Only current approved controllers can act; old-owner attempts do not mutate state. Exactly one primary connection/player association, intended extra owned objects, correctly rebound camera/input/UI and stable movement/physics writer. Test Host as old/new owner and server-only when supported. |
| M4: instance isolation | Run two matches or same-name additive instances. From A, use a known foreign target ID, custom match/interaction messages, projectile/raycast, collision and pickup attempts into B. Record server outcomes and recipient sets. | Server membership is valid; observers and message recipients are confined as designed; foreign interactions are rejected; physics/query results exclude the other instance. A client not seeing B alone is insufficient evidence. |
| M5: loading and room state | Select a character, Ready, join during transition, send duplicate/invalid selection, disconnect during load and return to room. Observe connections, player identities, selected state and load acknowledgements. | Phase gates hold; transfer preserves approved character/inventory state and authentication context. No premature spawn, duplicate player, invalid prefab index or orphaned pending join. Invalid/retried messages preserve a usable session. |
| M6: reuse policies | UnSpawn/respawn the same entity, then allocate that pooled object for a new logical entity; do both over Host and independent-client paths, including owner change and delayed respawn cancellation. | Continuing entity retains explicitly approved state. New entity starts with intended data/owner/velocity/buffers and one event binding. Each pool return/checkout occurs once; shutdown cannot respawn a stale entity. |
| M7: private replication | Change owner-private inventory/secret, observe from an unrelated client, revoke and transfer ownership, then change it again. Inspect new deliveries and permissions. | Unrelated observers never receive private state; the former owner cannot act or receive future private updates. Do not assert erasure of information already delivered to it. Public appearance still reconstructs for observers. |
| M8: repeat lifecycle | Repeat connect, load, play, disconnect, stop and restart with pending loads/respawns; compare scene, identity, manager, pool, static registry, timer and event-binding counts to baseline. | No residue or duplicate effects; task-owned processes/ports are released. A fresh session does not inherit transient state, and intentionally retained progress follows the chosen policy. |
| M9: movement and measured sync | Use real transport in independent processes under documented latency/jitter/loss. Exercise move/stop/collision, authority transfer and restart; measure convergence, correction, bandwidth and CPU where the feature requires it. | One motion writer, bounded divergence, usable gameplay and no Host double-force/damage. Prediction/Hybrid/lag compensation meets the measured need; a local delayed demo or benchmark scene cannot pass transport acceptance. |

## Test Layers

- EditMode: deterministic server rule validation, bounds/sequence/rate policies, configuration and prefab integrity using real Unity APIs.
- PlayMode: actual Mirror spawning, callbacks, state initialization, hook/collection UI, lifecycle and gameplay integration. Guard global NetworkServer/NetworkClient/static state and clean it in teardown.
- Independent processes: run the real transport and real gameplay assets. At least Host + one separate client; include server-only + clients when supported. For each action assert the authoritative result and remote observation, not just a successful socket connection.

Use explicit scenario IDs and bounded waits for join readiness and state convergence. Fixed long sleeps hide timing defects. For contention, use Host + two independent client players or server + two clients, hold both at an agreed barrier, then issue distinct authenticated requests. A single client's sequential calls cannot prove two-client contention. Run rule tests in EditMode, relevant integration assertions in PlayMode, and critical game paths over independent processes; do not replace one layer with another.

Do not assume Mirror can host multiple independent NetworkClient singletons in one Unity process. Use an existing project harness, Unity-supported multiplayer tooling, or separate Development Players. Discover any Player command-line bootstrap rather than inventing `-host`/`-client` flags. If no harness exists, implement a small test-only bootstrap using the installed API and create machine-readable assertion artifacts.

Launch each peer with isolated logs/ports/test output as needed. Keep stdout/stderr, process exit codes, version/commit, selected test names, topology, scenario inputs, and final assertions. Screenshots may support visual correctness but do not prove synchronization. Terminate only task-owned PIDs and retain failed-run evidence.

## Evidence and Final Report

Use the project's report convention, or an isolated `Reports/Networking/<run-id>/` for new artifacts. Keep credentials, raw Pipeline descriptors, save secrets and private player data out of reports.

Record baseline failures separately from new failures, affected test suites, start/end timestamps, exact selection, counts (passed/failed/skipped/inconclusive), errors, build result and topology. After a fix, retain the earlier failure and produce a fresh result. Results from before the final relevant edit cannot establish acceptance.

Report LAN/internet reachability separately from same-machine transport tests. Report reconnect as a fresh session unless identity/session restoration was implemented and tested. Do not imply host migration from ordinary reconnect behavior.
