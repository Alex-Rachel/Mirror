# Project Migration

## Inspect Before Choosing an Architecture

Use `rg` and structured JSON/asmdef parsing. Exclude generated directories such as Library, Temp, obj, Logs, Build, and .git from source searches. Locate the scripts actually referenced by the gameplay scenes/prefabs, not just similarly named files.

Read `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json`, `packages-lock.json`, local package metadata, test asmdefs, enabled build scenes/profiles, and the current entry scene. Mirror may be asset-based, embedded, cached, or a local/git UPM dependency. Determine the actual assembly/source imported by Unity and its version or commit. Missing version metadata is uncertainty, not permission to invent a version.

If Mirror is absent and conversion with Mirror is authorized, install a compatible, reproducible dependency through the project's established package/asset method. Respect the source license, retain metadata, avoid duplicate Mirror assemblies, and verify Unity imports/Weaver. Do not copy an entire local source checkout or third-party examples into the game. Existing NGO/Photon/custom networking needs an explicit migration decision; do not silently run two authorities.

Trace the original game loop from launch through session creation, player selection/spawn, movement, interactions, combat/AI, reward/progression, win/loss, and restart/exit. Find singleton/static state, local `Instantiate`/`Destroy`, direct health/currency setters, per-frame input/physics, scene loads, pools, and save writes that will behave differently with more than one player.

## Record Decisions That Affect Behavior

Keep the migration map small and concrete. One useful row is:

| Feature | Authority and input | Persistent state / events | Asset wiring | Acceptance |
| --- | --- | --- | --- | --- |
| Shared pickup | Owned player sends item intent; server checks proximity/availability | Server inventory delta + despawn; optional local effect | Registered pickup/player prefab | Two clients contend; exactly one grant; late join sees item gone |

Also record supported modes/platforms, max players, selected transport, address/port source, scene/round flow, disconnect policy, and what remains intentionally local. Infer reasonable defaults from the game. Matchmaking services, relay subscriptions, public hosting, account systems, purchases, host migration, and persistent cross-session identities are separate product choices, not automatic requirements of adding Mirror.

For the selected [example patterns](examples-patterns.md), record the source-relative path, key symbol, review date and reference revision, then the target's installed version/source and actual prefab/scene settings. Identify the tutorial gap the target must close and link its executable acceptance assertion. If version/import or saved wiring is unresolved, leave it explicit; a reference checkout or README does not settle it.

## Change the Actual Game

- Keep domain rules separate from presentation where that removes real coupling. A narrow networking adapter is often preferable to rewriting unrelated systems.
- Replace local ownership assumptions with per-player context. Only the local owned player receives gameplay input, active camera, audio listener, HUD binding, and local-only controls. Dedicated servers must work without camera, EventSystem, rendering, or local player.
- Move shared simulation and rewards to the chosen authority. AI/NavMesh/pathfinding and timers must not independently resolve the same outcomes on every client.
- Complete connection controls with address/port, Host/Join, pending state, usable errors, cancel/disconnect, and return to a stable menu/session state. Integrate existing UI; use NetworkManagerHUD only as a temporary debugging aid.
- Configure real prefabs/scenes: the project's manager or custom bootstrap and transport, player prefab, spawn positions, registered spawnables, supported network scene path, and build inclusion. Keep stable GUIDs and script references.
- Prefer Unity SerializedObject, PrefabUtility, scene APIs, or the installed project automation API for serialized edits. Save, reopen/reload, and validate affected assets. Never manufacture fileIDs/GUIDs with regex or treat a C# compile as proof of valid prefab wiring.
- Use deterministic Editor authoring utilities when needed; place them in editor-only assemblies. Report any utility retained as a deliberate maintenance tool.
- Keep owner-private state private; define how observers receive public appearance/state separately. Do not make all local save data world-readable to simplify synchronization.
- Handle rejected input, despawned targets, joining during transitions, disconnect during interaction, and repeated start/stop. Choose idempotency/sequence tokens for reward-bearing retryable requests when needed.

## Iterate

Implement and verify logical slices, then check integration. Preserve user edits and project conventions. Do not disable tests, weaken assertions, add broad catches, or suppress diagnostics to obtain green results. Unrelated baseline failures must be reported with evidence; they do not erase the need to run new multiplayer coverage.
