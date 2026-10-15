---
name: unity-mirror-multiplayer
description: "Convert or complete an existing Unity game into playable Mirror multiplayer, including gameplay authority, synchronized assets/scenes, connection flows, and verified regression tests. Use for Mirror 联机改造、单机转多人、联网功能完善 or fixing incomplete multiplayer. When Packages/com.unity.pipeline exists, executing automated tests is mandatory. Not for generic Unity work or silently replacing another networking stack."
---

# Unity Mirror Multiplayer

Deliver working multiplayer in the target game's actual gameplay loop. Implement code, prefabs, scenes, connection UI, and tests; a plan, sample scene, NetworkManagerHUD, or Host-only demonstration is not a completed conversion. Respond in the user's language.

Use [references/examples-patterns.md](references/examples-patterns.md) to select relevant examples by gameplay need; read the matching sections, then inspect their actual code and the target's saved components. Examples teach mechanisms and have production gaps.

## Establish the Target

The project named by the user is the target, even when the current workspace is a Mirror source checkout. `E:\WorkSpace\GundaProject` is an example, never an unconditional default. When asked only to create or edit this skill, do not modify that example or run its gameplay tests.

1. Read project instructions and working-tree changes. Run the read-only inventory:

   ```powershell
   python "<skill-dir>/scripts/audit_project.py" --project "<absolute-project>"
   ```

   Read [references/project-migration.md](references/project-migration.md) and follow the actual gameplay, assets and tests; inventory alone cannot validate wiring or absence of networking. Preserve user changes and serialized GUIDs.
2. Choose a topology compatible with the game. If unspecified, start with Host plus independent clients and configurable address/port; record this assumption. Separate localhost, LAN and internet acceptance.
3. Detect `<target>/Packages/com.unity.pipeline` as a directory **before edits and again before acceptance**. A directory or manifest/lock declaration requires automated testing even if the CLI fails. Read [references/pipeline-testing.md](references/pipeline-testing.md) when triggered.

## Implement Against the Installed Mirror

Read [references/mirror-contracts.md](references/mirror-contracts.md) for every conversion. Consult relevant current official pages:

- https://mirror-networking.gitbook.io/docs/manual/components
- https://mirror-networking.gitbook.io/docs/manual/general

If browsing is empty, try the official `.md` representation with available HTTP tools; report any documentation gap. Verify version/commit, APIs, callbacks, inheritance, serialization and saved prefab/scene settings against the **target's installed Mirror**. README, comments and another source checkout cannot override the actual imported implementation. Do not automatically upgrade Unity, Mirror or Pipeline.

Keep a compact feature map in the project's existing report location: original gameplay, example/source selected, authority/writer, request validation, recoverable state, assets/scenes and executable acceptance. Cover relevant systems, including those kept local. Preserve gameplay, AI, combat, progression and supported offline play; a replacement demo is insufficient.

Build playable slices through connect/spawn, local input/remote representation, core interactions, late join, transitions and repeat sessions. Reuse the project's UI, controllers, pools and persistence.

The server decides shared outcomes. Distinguish ownership, `syncDirection` and owner-only replication. Wire real entry scenes, spawnables, transport, player prefab, local camera/audio/input and connection failure UI; finish essential inspector assignments.

## Verify and Finish

Read [references/acceptance.md](references/acceptance.md). Compile in Unity, check Weaver and saved scenes/prefabs, then execute relevant assertions after the final edits.

- With Pipeline triggered, run relevant **EditMode and PlayMode** tests plus project regressions using the installed schema. The bundled `scripts/run_pipeline_tests.py` supports the verified older contract; use `--discover` first. Its CLI/JSON usage is in the Pipeline reference.
- Otherwise use the installed Unity Test Framework/batchmode or CI runner. Missing Unity leaves runtime acceptance incomplete.
- Execute automated critical gameplay over real transport in independent processes: Host plus remote client; server-only plus clients when supported. Single-process or local latency simulation cannot replace this.
- Missing/zero tests, compile-only, skipped required scenarios, stale results or API `success=true` do **not** pass a mandatory gate. Repair within scope; otherwise report the exact incomplete gate and leave reviewable work. Do not claim completion or production readiness.

Clean up task-owned processes, runs and temporary settings, and inspect the final diff. Report changed gameplay, run instructions, topology, counts, artifacts and limits. Claim internet reachability, security, scale, reconnect persistence or host migration only with corresponding evidence.
