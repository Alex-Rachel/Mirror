#!/usr/bin/env python3
"""Discover or run Unity Pipeline tests without depending on a matching CLI."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import json
from pathlib import Path
import socket
import time
import urllib.error
import urllib.request


class PipelineError(Exception):
    pass


class TransientPipelineError(PipelineError):
    pass


def field(value, name, default=None):
    if not isinstance(value, dict):
        return default
    return next((v for k, v in value.items() if k.lower() == name.lower()), default)


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def project_path(value):
    path = Path(value).expanduser()
    if not path.is_absolute():
        raise PipelineError("The project path must be absolute.")
    path = path.resolve()
    if not all((path / child).is_dir() for child in ("Assets", "Packages", "ProjectSettings")):
        raise PipelineError("The target is not an existing Unity project.")
    return path


def unwrap_exec(value):
    for _ in range(5):
        if isinstance(value, str):
            try:
                value = json.loads(value)
            except (ValueError, TypeError) as exc:
                raise PipelineError("Unexpected command response format.") from exc
        if not isinstance(value, dict):
            raise PipelineError("Expected an object from the Pipeline command.")
        if field(value, "success") is not True or field(value, "error"):
            raise PipelineError(str(field(value, "error") or "Pipeline command did not succeed."))
        nested = field(value, "result")
        if isinstance(nested, dict):
            value = nested
            continue
        if isinstance(nested, str) and nested.lstrip().startswith("{"):
            value = nested
            continue
        return value
    raise PipelineError("Pipeline command response is nested too deeply.")


def redact(value, secrets=()):
    if isinstance(value, dict):
        return {
            key: "<redacted>" if key.lower() in (
                "evaltoken", "authorization", "password", "accesstoken"
            ) else redact(item, secrets)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [redact(item, secrets) for item in value]
    if isinstance(value, str):
        for secret in secrets:
            if secret:
                value = value.replace(secret, "<redacted>")
    return value


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise PipelineError("Refusing an HTTP redirect from the local Pipeline server.")


class PipelineClient:
    def __init__(self, project, request_timeout=10):
        self.project = Path(project).resolve()
        self.descriptor = self.project / "Library/Pipeline/.unity-pipeline-port"
        self.request_timeout = request_timeout
        self.secrets = set()
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}), NoRedirect()
        )

    def read_descriptor(self):
        try:
            value = json.loads(self.descriptor.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError) as exc:
            raise TransientPipelineError("Pipeline descriptor is missing or temporarily unreadable.") from exc
        root = field(value, "projectPath")
        if not isinstance(root, str) or Path(root).resolve() != self.project:
            raise PipelineError("Pipeline descriptor belongs to a different project.")
        if field(value, "mode") not in ("editor", "batchmode"):
            raise PipelineError("Expected an Editor Pipeline descriptor.")
        pid, port, token = (field(value, key) for key in ("pid", "port", "evalToken"))
        if type(pid) is not int or pid <= 0 or type(port) is not int or not 1 <= port <= 65535:
            raise PipelineError("Pipeline descriptor has an invalid PID or port.")
        if not isinstance(token, str) or not token or "\n" in token or "\r" in token:
            raise PipelineError("Pipeline descriptor has an invalid authentication token.")
        self.secrets.add(token)
        return port, token

    def request(self, method, endpoint, deadline, payload=None):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TransientPipelineError("Client deadline reached.")
        port, token = self.read_descriptor()
        body = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(
            f"http://127.0.0.1:{port}{endpoint}",
            data=body, method=method,
            headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
        )
        try:
            with self.opener.open(request, timeout=min(self.request_timeout, remaining)) as response:
                raw = response.read(16 * 1024 * 1024 + 1)
                if len(raw) > 16 * 1024 * 1024:
                    raise PipelineError("Pipeline response exceeds the 16 MiB limit.")
                return json.loads(raw.decode("utf-8-sig"))
        except urllib.error.HTTPError as exc:
            error_type = TransientPipelineError if exc.code in (401, 408, 429, 500, 502, 503, 504) else PipelineError
            raise error_type(f"Pipeline HTTP request failed with status {exc.code}.") from exc
        except (urllib.error.URLError, socket.timeout, TimeoutError, ConnectionError, OSError) as exc:
            raise TransientPipelineError("Pipeline connection interrupted or unavailable.") from exc
        except (UnicodeError, ValueError) as exc:
            raise TransientPipelineError("Pipeline returned an unreadable JSON response.") from exc

    def get(self, endpoint, deadline):
        retry_deadline = min(deadline, time.monotonic() + 15)
        while True:
            try:
                return self.request("GET", endpoint, retry_deadline)
            except TransientPipelineError as exc:
                remaining = retry_deadline - time.monotonic()
                if remaining <= 0:
                    raise
                time.sleep(min(1, remaining))
                if time.monotonic() >= retry_deadline:
                    raise TransientPipelineError(
                        f"Pipeline read deadline reached; last error: {exc}"
                    ) from exc

    def execute(self, command, parameters, deadline):
        # Never replay a mutating POST: a lost response does not prove rejection.
        return unwrap_exec(self.request(
            "POST", "/api/exec", deadline,
            {"command": command, "parameters": parameters},
        ))


def validate_schema(payload):
    commands = field(payload, "commands")
    if not isinstance(commands, list):
        raise PipelineError("Command discovery returned an unsupported schema.")
    required = {
        "list_tests": {"mode"},
        "run_tests": {"mode", "filter", "filter_type", "include_explicit", "async_tests", "timeout"},
        "test_status": set(),
    }
    found = {}
    for command in commands:
        name = field(command, "name")
        if name in required:
            params = field(command, "parameters", [])
            if not isinstance(params, list):
                raise PipelineError(f"Unsupported parameter schema for {name}.")
            found[name] = sorted(str(field(param, "name", "")) for param in params)
    for name, params in required.items():
        if name not in found or not params.issubset(found[name]):
            raise PipelineError(f"Installed Pipeline schema is incompatible with {name}; inspect local docs/source.")
    return found


def test_status(client, deadline):
    value = client.get("/api/test-status", deadline)
    if not isinstance(value, dict) or not isinstance(field(value, "status"), str):
        raise PipelineError("Test status returned an unsupported schema.")
    return value


def wait_ready(client, deadline, interval):
    while True:
        value = client.get("/api/editor_status", deadline)
        root = field(value, "projectPath")
        if not isinstance(root, str) or Path(root).resolve() != client.project:
            raise PipelineError("The live Editor reports a different project.")
        if field(value, "playMode") in ("playing", "paused"):
            raise PipelineError("The Editor has an active play session; it was left untouched.")
        if (
            field(value, "status") == "ready"
            and field(value, "compiling") is False
            and field(value, "domainReloadInProgress") is False
            and field(value, "playMode") == "stopped"
        ):
            return {"unity_version": field(value, "unityVersion"), "project": root}
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise PipelineError("Editor readiness deadline reached.")
        time.sleep(min(interval, remaining))


def normalized_mode(value):
    if not isinstance(value, str):
        return None
    return {
        "editor": "EditMode", "editmode": "EditMode",
        "playmode": "PlayMode", "play": "PlayMode", "all": "All",
    }.get(value.casefold())


def list_tests(client, mode, deadline):
    value = client.execute("list_tests", {"mode": mode}, deadline)
    tests = field(value, "tests")
    count = field(value, "count")
    if not isinstance(tests, list) or type(count) is not int or count != len(tests):
        raise PipelineError("Test discovery did not provide a consistent test list.")
    expected_mode = normalized_mode(mode)
    if normalized_mode(field(value, "mode")) != expected_mode:
        raise PipelineError("Test discovery returned a different or missing mode.")
    leaf_modes = {"EditMode", "PlayMode"} if expected_mode == "All" else {expected_mode}
    for test in tests:
        if not isinstance(field(test, "fullName"), str) or not field(test, "fullName"):
            raise PipelineError("A discovered test has no full name.")
        if normalized_mode(field(test, "mode")) not in leaf_modes:
            raise PipelineError("A discovered test belongs to a different or missing mode.")
    return tests


def select_tests(tests, filter_text, filter_type, include_explicit=False):
    selected = []
    needle = (filter_text or "").casefold()
    for test in tests:
        if field(test, "explicit", False) and not include_explicit:
            continue
        if filter_type == "category":
            values = field(test, "categories", []) or []
            match = not needle or any(str(value).casefold() == needle for value in values)
        else:
            value = field(test, "assembly" if filter_type == "assembly" else "fullName", "") or ""
            match = needle in str(value).casefold()
        if match:
            selected.append(field(test, "fullName"))
    if len(selected) != len(set(selected)):
        raise PipelineError("Discovery contains duplicate full test names.")
    return sorted(selected)


def assess_results(value, selected, expected=(), allowed_skips=()):
    problems = []
    summary = field(value, "summary")
    counts = {key: field(summary, key) for key in ("total", "passed", "failed", "skipped", "inconclusive")}
    if any(type(count) is not int or count < 0 for count in counts.values()):
        return ["Missing or invalid test summary counts."]
    if counts["total"] == 0:
        problems.append("Zero tests executed.")
    if counts["passed"] == 0:
        problems.append("No tests passed; skipped cases alone cannot satisfy execution.")
    if counts["total"] != sum(counts[key] for key in ("passed", "failed", "skipped", "inconclusive")):
        problems.append("Test summary counts are inconsistent.")
    results = field(value, "results")
    if not isinstance(results, list):
        return problems + ["Missing leaf test results."]
    names = [field(result, "fullName") for result in results]
    if any(not isinstance(name, str) or not name for name in names):
        return problems + ["A leaf result has no test name."]
    if len(set(names)) != len(names):
        problems.append("Duplicate leaf test results.")
    if set(names) != set(selected):
        problems.append("Executed test names differ from the discovered selection.")
    if not set(expected).issubset(names):
        problems.append("Required scenario names are missing.")
    statuses = Counter(str(field(result, "status", "")).casefold() for result in results)
    if len(results) != counts["total"] or any(
        statuses[key] != counts[key] for key in ("passed", "failed", "skipped", "inconclusive")
    ) or set(statuses) - {"passed", "failed", "skipped", "inconclusive"}:
        problems.append("Leaf outcomes do not match the summary.")
    if counts["failed"] or counts["inconclusive"]:
        problems.append("Tests failed or were inconclusive.")
    for result in results:
        if str(field(result, "status", "")).casefold() == "skipped":
            name = field(result, "fullName")
            if name not in allowed_skips or name in expected:
                problems.append(f"Required or unapproved skipped test: {name}")
    return problems


def run(client, args, report):
    deadline = time.monotonic() + args.deadline_seconds
    report["editor"] = wait_ready(client, deadline, args.poll_seconds)
    report["command_parameters"] = validate_schema(client.get("/api/commands", deadline))
    baseline = test_status(client, deadline)
    if field(baseline, "status") not in ("no_tests", "completed", "error", "cancelled"):
        raise PipelineError("Another test run is active or its state is unknown; no run was started.")
    tests = list_tests(client, "all" if args.discover else args.mode, deadline)
    if args.discover:
        report.update(status="discovered", tests=tests, discovered_count=len(tests))
        return 0
    selected = select_tests(tests, args.filter, args.filter_type, args.include_explicit)
    report.update(
        mode=args.mode, filter=args.filter, filter_type=args.filter_type,
        include_explicit=args.include_explicit,
        selected_tests=selected, expected_tests=args.expected_test,
        allowed_skips=args.allow_skip,
    )
    if not selected:
        raise PipelineError("No tests match the selection; no run was started.")
    if not set(args.expected_test).issubset(selected):
        raise PipelineError("Expected test names are absent from the discovered selection.")
    # Repeat the active-run check immediately before the single start request.
    baseline = test_status(client, deadline)
    if field(baseline, "status") not in ("no_tests", "completed", "error", "cancelled"):
        raise PipelineError("Another test run started during discovery; it was left untouched.")
    acknowledged = False
    report["start_acknowledged"] = False
    report["test_run_may_be_active"] = True
    try:
        response = client.execute("run_tests", {
            "mode": args.mode, "filter": args.filter or "", "filter_type": args.filter_type,
            "include_explicit": args.include_explicit, "async_tests": True,
            "timeout": max(1, int(args.deadline_seconds)),
        }, deadline)
        if field(response, "result") != "running":
            raise PipelineError("Pipeline did not acknowledge an asynchronous run.")
        report["start_scope"] = {
            "mode": field(response, "mode"),
            "filter_applied": field(response, "filterApplied"),
        }
        if normalized_mode(field(response, "mode")) != normalized_mode(args.mode):
            raise PipelineError("Pipeline start acknowledgement returned a different or missing mode.")
        expected_filter = f"{args.filter_type.lower()}: {args.filter}" if args.filter else None
        if field(response, "filterApplied") != expected_filter:
            raise PipelineError("Pipeline start acknowledgement returned a different filter or filter type.")
        acknowledged = True
        report["start_acknowledged"] = True
    except TransientPipelineError:
        report["start_acknowledged"] = False
        report["start_warning"] = "Start response was interrupted; polling without replaying the request."
    changed_since_baseline = False
    while time.monotonic() < deadline:
        status = test_status(client, deadline)
        changed_since_baseline = changed_since_baseline or status != baseline
        state = field(status, "status")
        if state == "completed" and (acknowledged or changed_since_baseline):
            report["test_run_may_be_active"] = False
            report["result"] = status
            if not acknowledged:
                raise PipelineError("Completion could not be correlated to an acknowledged start; results are indeterminate.")
            problems = assess_results(status, selected, args.expected_test, args.allow_skip)
            report.update(status="failed" if problems else "passed", problems=problems)
            return 1 if problems else 0
        if state in ("error", "cancelled", "no_tests") and (acknowledged or changed_since_baseline):
            report.update(result=status, test_run_may_be_active=False)
            raise PipelineError(f"Test execution ended with status {state}.")
        if state not in ("running", "queued", "completed", "error", "cancelled", "no_tests"):
            report["result"] = status
            raise PipelineError(f"Unsupported test run status: {state}")
        time.sleep(min(args.poll_seconds, max(0, deadline - time.monotonic())))
    raise PipelineError("Client deadline reached; inspect status and cancel only a run owned by this task.")


def report_path(value, project):
    if not value:
        return None
    path = Path(value).expanduser()
    if not path.is_absolute():
        raise PipelineError("The report path must be absolute.")
    path = path.resolve()
    if path.suffix.lower() != ".json" or path.exists():
        raise PipelineError("The report must be a new .json file; overwriting is refused.")
    try:
        relative = path.relative_to(project)
    except ValueError:
        relative = None
    if relative and relative.parts[0].casefold() in {
        "assets", "packages", "projectsettings", "library", "temp", "usersettings", ".git"
    }:
        raise PipelineError("Reports cannot be written into project source, settings, or generated state.")
    if any(parent.exists() and not parent.is_dir() for parent in path.parents):
        raise PipelineError("A report parent is not a directory.")
    return path


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True)
    parser.add_argument("--discover", action="store_true", help="Read-only test/schema discovery; never starts tests.")
    parser.add_argument("--mode", choices=("editor", "playmode"))
    parser.add_argument("--filter", default="")
    parser.add_argument("--filter-type", choices=("assembly", "testName", "category"), default="assembly")
    parser.add_argument("--include-explicit", action="store_true")
    parser.add_argument("--expected-test", action="append", default=[])
    parser.add_argument("--allow-skip", action="append", default=[])
    parser.add_argument("--deadline-seconds", type=float, default=600)
    parser.add_argument("--poll-seconds", type=float, default=2)
    parser.add_argument("--request-timeout", type=float, default=10)
    parser.add_argument("--report", help="Absolute path to a new JSON artifact; existing files are never overwritten.")
    args = parser.parse_args(argv)
    if not args.discover and not args.mode:
        parser.error("--mode editor|playmode is required unless --discover is used")
    for name in ("deadline_seconds", "poll_seconds", "request_timeout"):
        number = getattr(args, name)
        if not 0 < number < float("inf"):
            parser.error(f"--{name.replace('_', '-')} must be finite and positive")
    args.poll_seconds = min(args.poll_seconds, 30)
    report = {"schema_version": 1, "started_at": utc_now(), "status": "incomplete"}
    client = None
    output_path = None
    try:
        project = project_path(args.project)
        output_path = report_path(args.report, project)
        report["project"] = str(project)
        report["operation"] = "discover" if args.discover else "run_tests"
        client = PipelineClient(project, args.request_timeout)
        exit_code = run(client, args, report)
    except (PipelineError, OSError) as exc:
        report["error"] = str(exc)
        exit_code = 2
    except KeyboardInterrupt:
        report["error"] = "Interrupted; inspect status before starting another test run."
        exit_code = 2
    report["finished_at"] = utc_now()
    report = redact(report, client.secrets if client else ())
    if output_path is not None:
        try:
            output_path.parent.mkdir(parents=True, exist_ok=True)
            with output_path.open("x", encoding="utf-8") as stream:
                json.dump(report, stream, ensure_ascii=True, indent=2)
                stream.write("\n")
        except OSError:
            report["report_error"] = "Could not create the report artifact; stdout still contains the result."
            exit_code = 2
    print(json.dumps(report, ensure_ascii=True, indent=2))
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
