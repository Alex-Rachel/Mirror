"""Offline HTTP regressions for the Unity Pipeline runner."""

from collections import Counter
from contextlib import redirect_stdout
from copy import deepcopy
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import io
import json
from pathlib import Path
import socket
import tempfile
import threading
import unittest


RUNNER_PATH = Path(__file__).resolve().parents[1] / "run_pipeline_tests.py"
SPEC = importlib.util.spec_from_file_location("pipeline_runner_under_test", RUNNER_PATH)
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)

TEST_NAME = "MirrorIntegrationTests.RemoteClientDamageIsAuthoritative"
ASSEMBLY = "MirrorIntegrationTests"


def uppercase_keys(value):
    if isinstance(value, dict):
        return {key.upper(): uppercase_keys(item) for key, item in value.items()}
    if isinstance(value, list):
        return [uppercase_keys(item) for item in value]
    return value


def completed(results=None):
    results = results if results is not None else [
        {"FullName": TEST_NAME, "Status": "Passed"}
    ]
    counts = Counter(result["Status"].casefold() for result in results)
    return {
        "status": "completed",
        "summary": {
            "total": len(results),
            "passed": counts["passed"],
            "failed": counts["failed"],
            "skipped": counts["skipped"],
            "inconclusive": counts["inconclusive"],
        },
        "results": results,
    }


class Response:
    def __init__(self, payload=None, status=200, headers=None, disconnect=False):
        self.payload = payload
        self.status = status
        self.headers = headers or {}
        self.disconnect = disconnect


class FakePipeline:
    def __init__(self, root):
        self.project = root / "UnityProject"
        for name in ("Assets", "Packages", "ProjectSettings", "Library/Pipeline"):
            (self.project / name).mkdir(parents=True, exist_ok=True)
        self.descriptor_path = self.project / "Library/Pipeline/.unity-pipeline-port"
        self.token = "fake-loopback-token"
        self.requests = []
        self.errors = []
        self.get_hook = None
        self.started = False
        self.disconnect_start = False
        self.nested_wrappers = False
        self.uppercase = False
        self.idle_statuses = [{"status": "no_tests"}]
        self.statuses = [completed()]
        self.editor = {
            "projectPath": str(self.project),
            "status": "ready",
            "compiling": False,
            "domainReloadInProgress": False,
            "playMode": "stopped",
            "unityVersion": "6000.3.0f1",
        }
        self.commands = {
            "commands": [
                {"name": "list_tests", "parameters": [{"name": "mode"}]},
                {
                    "name": "run_tests",
                    "parameters": [{"name": name} for name in (
                        "mode", "filter", "filter_type", "include_explicit",
                        "async_tests", "timeout",
                    )],
                },
                {"name": "test_status", "parameters": []},
            ]
        }
        self.discovery = {
            "Success": True,
            "Command": "list_tests",
            "Mode": "PlayMode",
            "Count": 1,
            "Tests": [{
                "FullName": TEST_NAME,
                "Mode": "PlayMode",
                "Assembly": ASSEMBLY,
                "Categories": ["Multiplayer"],
                "Explicit": False,
            }],
        }
        self.start = {
            "Success": True,
            "Command": "run_tests",
            "Result": "running",
            "Mode": "PlayMode",
            "FilterApplied": "assembly: MirrorIntegrationTests",
        }
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), FakeHandler)
        self.server.fixture = self
        self.server.daemon_threads = True
        self.write_descriptor()
        self.thread = threading.Thread(
            target=self.server.serve_forever,
            kwargs={"poll_interval": 0.01},
            daemon=True,
        )
        self.thread.start()

    def write_descriptor(self, **changes):
        value = {
            "projectPath": str(self.project),
            "mode": "editor",
            "pid": 12345,
            "port": self.server.server_port,
            "evalToken": self.token,
        }
        value.update(changes)
        self.descriptor_path.write_text(json.dumps(value), encoding="utf-8")

    def wrap(self, value):
        value = uppercase_keys(value) if self.uppercase else value
        if self.nested_wrappers:
            return {
                "SUCCESS": True,
                "RESULT": json.dumps({"Success": True, "Result": value}),
            }
        return {"success": True, "result": value}

    @staticmethod
    def next_status(values):
        value = values[0]
        if len(values) > 1:
            values.pop(0)
        return value

    def respond(self, method, path, authorization, payload):
        self.requests.append((method, path, authorization, payload))
        if self.get_hook and method == "GET":
            response = self.get_hook(path, authorization)
            if response is not None:
                return response
        if authorization != f"Bearer {self.token}":
            return Response({"error": "Unauthorized"}, status=401)
        if method == "GET":
            routes = {
                "/api/editor_status": self.editor,
                "/api/commands": self.commands,
            }
            if path == "/api/test-status":
                value = self.next_status(
                    self.statuses if self.started else self.idle_statuses
                )
            elif path in routes:
                value = routes[path]
            else:
                return Response({"error": "Unknown route"}, status=404)
            return Response(uppercase_keys(value) if self.uppercase else value)
        if path != "/api/exec":
            return Response({"error": "Unknown route"}, status=404)
        command = payload["command"]
        if command == "list_tests":
            listing = deepcopy(self.discovery)
            if payload["parameters"]["mode"] == "all" and listing.get("Mode") == "PlayMode":
                listing["Mode"] = "All"
            return Response(self.wrap(listing))
        if command == "run_tests":
            self.started = True
            return Response(self.wrap(self.start), disconnect=self.disconnect_start)
        return Response({"success": False, "error": "Unknown command"})

    @property
    def starts(self):
        return [
            payload for method, _, _, payload in self.requests
            if method == "POST" and payload.get("command") == "run_tests"
        ]

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)


class FakeHandler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        self.handle_request("GET")

    def do_POST(self):
        self.handle_request("POST")

    def handle_request(self, method):
        fixture = self.server.fixture
        try:
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length)) if length else None
            response = fixture.respond(
                method, self.path, self.headers.get("Authorization"), payload
            )
            if response.disconnect:
                self.connection.shutdown(socket.SHUT_RDWR)
                self.connection.close()
                return
            raw = json.dumps(response.payload).encode("utf-8")
            self.send_response(response.status)
            for name, value in response.headers.items():
                self.send_header(name, value)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as exc:
            fixture.errors.append(exc)
            self.connection.close()


class PipelineRunnerHttpTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="mirror-skill-tests-")
        self.root = Path(self.temporary.name)
        self.pipeline = FakePipeline(self.root)

    def tearDown(self):
        self.pipeline.close()
        self.temporary.cleanup()
        self.assertEqual(self.pipeline.errors, [], "Fake server raised an error")

    def invoke(self, *arguments, discover=False, deadline="2"):
        argv = [
            "--project", str(self.pipeline.project),
            "--deadline-seconds", deadline,
            "--poll-seconds", "0.01",
            "--request-timeout", "0.1",
        ]
        argv += ["--discover"] if discover else [
            "--mode", "playmode", "--filter", ASSEMBLY,
        ]
        argv += list(arguments)
        output = io.StringIO()
        with redirect_stdout(output):
            code = RUNNER.main(argv)
        raw = output.getvalue()
        return code, json.loads(raw), raw

    def assert_not_passed(self, outcome):
        code, report, _ = outcome
        self.assertNotEqual(code, 0)
        self.assertNotEqual(report["status"], "passed")

    def test_discovery_never_starts_tests(self):
        code, report, _ = self.invoke(discover=True)
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "discovered")
        self.assertEqual(report["discovered_count"], 1)
        self.assertEqual(self.pipeline.starts, [])
        discovery_requests = [
            payload for method, _, _, payload in self.pipeline.requests
            if method == "POST"
        ]
        self.assertEqual(discovery_requests, [{
            "command": "list_tests", "parameters": {"mode": "all"},
        }])

    def test_nested_json_wrappers_and_uppercase_fields(self):
        self.pipeline.nested_wrappers = True
        self.pipeline.uppercase = True
        code, report, _ = self.invoke()
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "passed")
        self.assertEqual(len(self.pipeline.starts), 1)

    def test_passed_run_reports_complete_selected_coverage(self):
        code, report, _ = self.invoke("--expected-test", TEST_NAME)
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "passed")
        self.assertEqual(report["selected_tests"], [TEST_NAME])
        self.assertFalse(report["test_run_may_be_active"])
        parameters = self.pipeline.starts[0]["parameters"]
        self.assertEqual(parameters["mode"], "playmode")
        self.assertEqual(parameters["filter"], ASSEMBLY)
        self.assertEqual(parameters["filter_type"], "assembly")
        self.assertTrue(parameters["async_tests"])
        self.assertFalse(parameters["include_explicit"])

    def test_editor_request_accepts_source_editmode_alias(self):
        self.pipeline.discovery["Mode"] = "EditMode"
        self.pipeline.discovery["Tests"][0]["Mode"] = "EditMode"
        self.pipeline.start["Mode"] = "EditMode"
        code, report, _ = self.invoke("--mode", "editor")
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "passed")

    def test_category_filter_uses_exact_category_match(self):
        self.pipeline.start["FilterApplied"] = "category: Multiplayer"
        code, report, _ = self.invoke(
            "--filter-type", "category", "--filter", "Multiplayer"
        )
        self.assertEqual(code, 0)
        self.assertEqual(report["selected_tests"], [TEST_NAME])

    def test_testname_filter_accepts_source_normalized_filter_type(self):
        self.pipeline.start["FilterApplied"] = "testname: RemoteClientDamage"
        code, report, _ = self.invoke(
            "--filter-type", "testName", "--filter", "RemoteClientDamage"
        )
        self.assertEqual(code, 0)
        self.assertEqual(report["selected_tests"], [TEST_NAME])

    def test_empty_filter_accepts_null_filter_applied(self):
        self.pipeline.start["FilterApplied"] = None
        code, report, _ = self.invoke("--filter", "")
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "passed")

    def test_outer_success_cannot_hide_failed_leaf(self):
        self.pipeline.statuses = [completed([
            {"FullName": TEST_NAME, "Status": "Failed"},
        ])]
        self.assert_not_passed(self.invoke())
        self.assertEqual(len(self.pipeline.starts), 1)

    def test_inconclusive_is_not_a_pass(self):
        self.pipeline.statuses = [completed([
            {"FullName": TEST_NAME, "Status": "Inconclusive"},
        ])]
        self.assert_not_passed(self.invoke())

    def test_zero_executed_tests_is_not_a_pass(self):
        self.pipeline.statuses = [completed([])]
        self.assert_not_passed(self.invoke())

    def test_zero_discovered_tests_cannot_start(self):
        self.pipeline.discovery.update(Count=0, Tests=[])
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.starts, [])

    def test_active_run_is_left_untouched(self):
        self.pipeline.idle_statuses = [{"status": "running"}]
        self.assert_not_passed(self.invoke())
        self.assertFalse(self.pipeline.started)
        self.assertTrue(all(method == "GET" for method, _, _, _ in self.pipeline.requests))

    def test_run_starting_during_discovery_is_left_untouched(self):
        self.pipeline.idle_statuses = [
            {"status": "no_tests"}, {"status": "queued"},
        ]
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.starts, [])

    def test_explicit_tests_are_excluded_by_default(self):
        explicit_name = "MirrorIntegrationTests.ExplicitStressTest"
        self.pipeline.discovery["Tests"].append({
            "FullName": explicit_name, "Mode": "PlayMode",
            "Assembly": ASSEMBLY, "Categories": [], "Explicit": True,
        })
        self.pipeline.discovery["Count"] = 2
        code, report, _ = self.invoke()
        self.assertEqual(code, 0)
        self.assertEqual(report["selected_tests"], [TEST_NAME])

    def test_missing_required_name_cannot_start(self):
        self.assert_not_passed(self.invoke(
            "--expected-test", "MirrorIntegrationTests.MissingScenario"
        ))
        self.assertEqual(self.pipeline.starts, [])

    def test_wrong_mode_in_discovery_cannot_start(self):
        self.pipeline.discovery["Mode"] = "EditMode"
        self.pipeline.discovery["Tests"][0]["Mode"] = "EditMode"
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.starts, [])

    def test_wrong_leaf_mode_in_discovery_cannot_start(self):
        self.pipeline.discovery["Tests"][0]["Mode"] = "EditMode"
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.starts, [])

    def test_wrong_mode_in_start_acknowledgement_is_not_a_pass(self):
        self.pipeline.start["Mode"] = "EditMode"
        outcome = self.invoke()
        self.assert_not_passed(outcome)
        self.assertTrue(outcome[1]["test_run_may_be_active"])
        self.assertFalse(outcome[1]["start_acknowledged"])
        self.assertEqual(len(self.pipeline.starts), 1)

    def test_wrong_filter_in_start_acknowledgement_is_not_a_pass(self):
        self.pipeline.start["FilterApplied"] = "assembly: UnrelatedTests"
        outcome = self.invoke()
        self.assert_not_passed(outcome)
        self.assertTrue(outcome[1]["test_run_may_be_active"])
        self.assertFalse(outcome[1]["start_acknowledged"])
        self.assertEqual(len(self.pipeline.starts), 1)

    def test_wrong_filter_type_in_start_acknowledgement_is_not_a_pass(self):
        self.pipeline.start["FilterApplied"] = f"category: {ASSEMBLY}"
        outcome = self.invoke()
        self.assert_not_passed(outcome)
        self.assertTrue(outcome[1]["test_run_may_be_active"])
        self.assertFalse(outcome[1]["start_acknowledged"])

    def test_wrong_executed_names_are_not_a_pass(self):
        self.pipeline.statuses = [completed([
            {"FullName": "OtherTests.Unrelated", "Status": "Passed"},
        ])]
        self.assert_not_passed(self.invoke())

    def test_duplicate_leaf_names_are_not_a_pass(self):
        self.pipeline.statuses = [completed([
            {"FullName": TEST_NAME, "Status": "Passed"},
            {"FullName": TEST_NAME, "Status": "Passed"},
        ])]
        self.assert_not_passed(self.invoke())

    def test_inconsistent_summary_is_not_a_pass(self):
        result = completed()
        result["summary"]["total"] = 2
        self.pipeline.statuses = [result]
        self.assert_not_passed(self.invoke())

    def test_leaf_outcomes_must_match_summary(self):
        result = completed()
        result["summary"].update(passed=0, failed=1)
        self.pipeline.statuses = [result]
        self.assert_not_passed(self.invoke())

    def test_discovery_count_must_match_list(self):
        self.pipeline.discovery["Count"] = 2
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.starts, [])

    def test_skipped_required_test_is_not_passed_even_when_allowlisted(self):
        self.pipeline.statuses = [completed([
            {"FullName": TEST_NAME, "Status": "Skipped"},
        ])]
        self.assert_not_passed(self.invoke(
            "--expected-test", TEST_NAME, "--allow-skip", TEST_NAME
        ))

    def test_unapproved_skip_is_not_a_pass(self):
        self.pipeline.statuses = [completed([
            {"FullName": TEST_NAME, "Status": "Skipped"},
        ])]
        self.assert_not_passed(self.invoke())

    def test_allowlisted_nonrequired_skip_is_accepted(self):
        optional_name = "MirrorIntegrationTests.OptionalDedicatedServerStress"
        self.pipeline.discovery["Tests"].append({
            "FullName": optional_name, "Mode": "PlayMode",
            "Assembly": ASSEMBLY, "Categories": [], "Explicit": False,
        })
        self.pipeline.discovery["Count"] = 2
        self.pipeline.statuses = [completed([
            {"FullName": TEST_NAME, "Status": "Passed"},
            {"FullName": optional_name, "Status": "Skipped"},
        ])]
        code, report, _ = self.invoke(
            "--expected-test", TEST_NAME, "--allow-skip", optional_name
        )
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "passed")

    def test_selection_with_only_allowlisted_skips_is_not_a_pass(self):
        self.pipeline.statuses = [completed([
            {"FullName": TEST_NAME, "Status": "Skipped"},
        ])]
        self.assert_not_passed(self.invoke("--allow-skip", TEST_NAME))

    def test_ambiguous_start_disconnect_is_never_replayed_or_passed(self):
        self.pipeline.disconnect_start = True
        self.pipeline.statuses = [{"status": "running"}, completed()]
        code, report, _ = self.invoke()
        self.assertNotEqual(code, 0)
        self.assertNotEqual(report["status"], "passed")
        self.assertEqual(len(self.pipeline.starts), 1)
        self.assertFalse(report["start_acknowledged"])

    def test_deadline_does_not_pass_retained_results_after_ambiguous_start(self):
        retained = completed()
        self.pipeline.idle_statuses = [deepcopy(retained)]
        self.pipeline.statuses = [deepcopy(retained)]
        self.pipeline.disconnect_start = True
        code, report, _ = self.invoke(deadline="0.15")
        self.assertNotEqual(code, 0)
        self.assertNotEqual(report["status"], "passed")
        self.assertEqual(len(self.pipeline.starts), 1)
        self.assertTrue(report["test_run_may_be_active"])

    def test_identical_completed_results_are_valid_after_verified_ack(self):
        retained = completed()
        self.pipeline.idle_statuses = [deepcopy(retained)]
        self.pipeline.statuses = [deepcopy(retained)]
        code, report, _ = self.invoke()
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "passed")
        self.assertEqual(len(self.pipeline.starts), 1)
        self.assertTrue(report["start_acknowledged"])

    def test_running_until_deadline_is_not_a_pass_or_cancelled(self):
        self.pipeline.statuses = [{"status": "running"}]
        code, report, _ = self.invoke(deadline="0.15")
        self.assertNotEqual(code, 0)
        self.assertNotEqual(report["status"], "passed")
        self.assertTrue(report["test_run_may_be_active"])
        commands = [
            payload["command"] for method, _, _, payload in self.pipeline.requests
            if method == "POST"
        ]
        self.assertNotIn("cancel_tests", commands)

    def test_token_rotation_retries_get_with_current_descriptor(self):
        first_token = self.pipeline.token
        rotated = []

        def rotate_once(path, _):
            if path == "/api/editor_status" and not rotated:
                rotated.append(True)
                self.pipeline.token = "fake-new-token"
                self.pipeline.write_descriptor()
                return Response({"error": "Unauthorized"}, status=401)
            return None

        self.pipeline.get_hook = rotate_once
        code, report, raw = self.invoke(discover=True)
        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "discovered")
        attempts = [
            authorization for method, path, authorization, _ in self.pipeline.requests
            if method == "GET" and path == "/api/editor_status"
        ]
        self.assertEqual(attempts, [f"Bearer {first_token}", "Bearer fake-new-token"])
        self.assertNotIn(first_token, raw)
        self.assertNotIn(self.pipeline.token, raw)

    def test_descriptor_for_different_project_sends_no_requests(self):
        self.pipeline.write_descriptor(projectPath=str(self.root / "OtherProject"))
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.requests, [])

    def test_missing_descriptor_keeps_actionable_cause_after_retry_deadline(self):
        self.pipeline.descriptor_path.unlink()
        outcome = self.invoke(deadline="0.05")
        self.assert_not_passed(outcome)
        self.assertIn("descriptor", outcome[1]["error"].casefold())
        self.assertEqual(self.pipeline.requests, [])

    def test_live_editor_for_different_project_cannot_start(self):
        self.pipeline.editor["projectPath"] = str(self.root / "OtherProject")
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.starts, [])

    def test_active_play_session_is_left_untouched(self):
        self.pipeline.editor["playMode"] = "playing"
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.starts, [])

    def test_incompatible_schema_cannot_start(self):
        self.pipeline.commands["commands"][1]["parameters"] = [{"name": "mode"}]
        self.assert_not_passed(self.invoke())
        self.assertEqual(self.pipeline.starts, [])

    def test_report_matches_stdout_and_redacts_keys_and_known_tokens(self):
        secret = self.pipeline.token
        result = completed()
        result["results"][0].update({
            "Message": f"Server echoed {secret}",
            "Nested": {"EVALTOKEN": "another-secret", "Authorization": "Bearer hidden"},
        })
        self.pipeline.statuses = [result]
        artifact = self.root / "artifacts" / "result.json"
        code, report, raw = self.invoke("--report", str(artifact))
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(artifact.read_text(encoding="utf-8")), report)
        self.assertNotIn(secret, raw)
        self.assertNotIn("another-secret", raw)
        self.assertNotIn("Bearer hidden", raw)
        self.assertIn("<redacted>", raw)

    def test_server_error_redacts_known_token(self):
        self.pipeline.discovery = {
            "Success": False, "Error": f"Error echoed {self.pipeline.token}",
        }
        code, _, raw = self.invoke()
        self.assertNotEqual(code, 0)
        self.assertNotIn(self.pipeline.token, raw)

    def test_report_into_project_source_is_rejected_before_http(self):
        for directory in (
            "Assets", "Packages", "ProjectSettings", "Library",
            "Temp", "UserSettings", ".git",
        ):
            with self.subTest(directory=directory):
                artifact = self.pipeline.project / directory / "result.json"
                self.assert_not_passed(self.invoke("--report", str(artifact)))
                self.assertFalse(artifact.exists())
        self.assertEqual(self.pipeline.requests, [])

    def test_existing_report_is_never_overwritten(self):
        artifact = self.root / "existing.json"
        artifact.write_text('{"preserved": true}', encoding="utf-8")
        self.assert_not_passed(self.invoke("--report", str(artifact)))
        self.assertEqual(artifact.read_text(encoding="utf-8"), '{"preserved": true}')
        self.assertEqual(self.pipeline.requests, [])

    def test_relative_report_is_rejected_before_http(self):
        self.assert_not_passed(self.invoke("--report", "result.json"))
        self.assertEqual(self.pipeline.requests, [])

    def test_redirect_is_refused_without_contacting_redirect_target(self):
        target = FakePipeline(self.root / "RedirectTarget")
        self.addCleanup(target.close)

        def redirect(path, _):
            if path == "/api/editor_status":
                return Response(
                    {}, status=302,
                    headers={"Location": f"http://127.0.0.1:{target.server.server_port}/secret"},
                )
            return None

        self.pipeline.get_hook = redirect
        self.assert_not_passed(self.invoke())
        self.assertEqual(target.requests, [])
        self.assertEqual(self.pipeline.starts, [])


if __name__ == "__main__":
    unittest.main()
