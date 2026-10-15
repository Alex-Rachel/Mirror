# Pipeline Testing

## Mandatory Trigger

If `<target>/Packages/com.unity.pipeline` is a directory, executing automated tests is required. This exact condition applies even if the package is embedded without a manifest entry. Manifest/lock package declarations also trigger testing. Do not delete/rename the package, omit the trigger, or downgrade to static checks to bypass it.

First read the **installed** package's `package.json`, README and relevant `Documentation~`/source. Discover available commands and test assemblies before invoking them. Do not depend on another skill being installed. No automatic package/CLI upgrade is necessary.

## Connection and Compatibility

Use the installed Unity CLI if compatible:

```powershell
unity command --project-path "<absolute-project>"
```

Project selection goes before the command name. Check actual CLI help for flags and JSON output. A newer CLI may refuse named commands for an older Pipeline package; use the verified HTTP API below instead of upgrading the project.

The bundled helper implements this contract and re-reads the descriptor when polling across domain reloads:

```powershell
python "<skill-dir>/scripts/run_pipeline_tests.py" --project "<absolute-project>" --discover
python "<skill-dir>/scripts/run_pipeline_tests.py" --project "<absolute-project>" --mode editor --filter "Game.Networking.EditorTests" --filter-type assembly --report "<absolute-new-editor-report.json>"
python "<skill-dir>/scripts/run_pipeline_tests.py" --project "<absolute-project>" --mode playmode --filter "Game.Networking.PlayModeTests" --filter-type assembly --report "<absolute-new-playmode-report.json>"
```

The filter names above are placeholders for **discovered real assemblies**, not names to paste blindly. Report paths must be absolute, point to new `.json` files, and stay outside project source/settings/generated-state directories. Omit `--filter` to run the whole chosen mode. Run existing project regressions as well as the new relevant networking coverage. `--expected-test` can require exact discovered scenario names. Nonzero helper exit is a failed/incomplete gate; inspect the JSON report.

Use `--discover` only for read-only discovery while creating/testing this skill; it does not satisfy project test execution. The helper does not compile, build, implement tests, automate multiple Players, or prove internet reachability.

## Verified HTTP Contract

Verified against `com.unity.pipeline` `0.3.1-exp.1` in the example project on 2026-10-14. Treat the example as a compatibility case, not a version pin or default project.

1. Privately read `<target>/Library/Pipeline/.unity-pipeline-port`. Validate `projectPath`, editor mode, positive PID, port and token. Never echo/persist the descriptor or `evalToken`.
2. Request `http://127.0.0.1:<port>` with `Authorization: Bearer <evalToken>`. Do not expose the server remotely, use browser-origin requests, follow redirects with credentials, or pass the token in a command line. Verify `/api/editor_status` reports the selected project.
3. Wait for compilation/domain reload to finish. Do not stop a user's ongoing play session. Refuse a new test run while `/api/test-status` is running: starting `run_tests` invalidates the previous run in this version.
4. GET `/api/commands` and verify commands/parameter schema. Discover tests with POST `/api/exec`:

   ```json
   {"command":"list_tests","parameters":{"mode":"all"}}
   ```

5. Recompile after a logical set of edits and poll `recompile_status`. Use the installed `recompile`/status source and inspect compiler/Weaver errors; a queued response is not completion. Re-discover tests after adding or changing test assemblies.
6. Run modes **separately** and asynchronously:

   ```json
   {"command":"run_tests","parameters":{"mode":"playmode","filter":"Actual.Assembly","filter_type":"assembly","include_explicit":false,"async_tests":true,"timeout":300}}
   ```

7. Poll GET `/api/test-status` until terminal status. The endpoint uses `/api/test-status`, not the documentation shorthand `/test-status`. `/api/exec` has a nested `result`; calling `test_status` as a command may return a JSON string inside it.

PlayMode synchronous HTTP is unsupported because entering play can reload the domain. In the verified version `mode=all` + async is unsupported, and synchronous `all` returns EditMode results while PlayMode is still running. Its async `timeout` argument is not enforced server-side; impose a client deadline.

Missing descriptor/refused connection: locate or start the matching installed Unity editor with this project and wait for import/server initialization, provided this is within the requested project work. Do not open a second editor on a project already in use. Re-read the descriptor after reload/401; port and token may rotate. Retry safe reads within a deadline. Never blindly replay `run_tests` after an ambiguous disconnected POST; poll first. If acceptance cannot be correlated to this run, mark it indeterminate.

## Pass Criteria and Recovery

HTTP 200, outer/inner `success=true`, or `status=completed` alone is insufficient. Require:

- Fresh results from the acknowledged run, the selected mode/filters, and the selected expected scenario names.
- Nonzero executed selection with at least one passing test; the required tests exist and run. An entirely skipped selection does not satisfy execution, even with an allowlist.
- Zero failed and inconclusive cases; no skipped required scenarios. Explicitly document any allowed skip in a broader regression suite.
- Consistent aggregate counts and leaf results. Preserve each failure and stack trace.

The helper rejects zero tests, missing selected names, inconsistent counts, unexpected skipped tests and unacknowledged results. It refuses an active test run and never retries the mutating start request. Its default deadline is bounded; timeout is incomplete and may leave a run active. Inspect status and cancel **only the test run you started**, using the installed `cancel_tests` contract when ownership is clear. Do not cancel another session or accept the last run's retained status as new proof.

In the verified version, discovery and start acknowledgements expose `Mode`; start also exposes `FilterApplied` with a lowercase filter type. Terminal status exposes neither mode/filter nor a run ID. The helper validates the discovered mode, each test's mode, and the acknowledged scope. Freshness after a verified acknowledgement relies on the installed runner deleting its previous status before acknowledging; a fast new run may produce identical result bytes. A lost acknowledgement remains indeterminate even if polling sees completion. Coordinate exclusive use of the Editor's test runner during acceptance, and inspect changed contracts before adapting the helper to another version.

If EditMode or PlayMode networking coverage is absent, implement suitable project tests and rerun discovery. Existing Pipeline-package tests do not replace tests of the game's networking. Use Unity test assembly references appropriate to the installed Mirror/Test Framework; do not put NUnit/test code in production assemblies.

For independent Player automation, inspect `Documentation~/runtime-setup.md` and the installed runtime APIs. Development Players with the runtime manager enabled may expose local automation. Keep this diagnostic/eval capability out of production builds; ensure release configurations disable it. Capture separate process logs and assert the actual network/gameplay results as described in [acceptance.md](acceptance.md).
