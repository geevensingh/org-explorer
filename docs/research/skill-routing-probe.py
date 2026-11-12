"""Probe real CLI skill routing without inference, workplace access, or UI."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


NAME = "skill-routing-probe"
BODY = "SYNTHETIC_USER_ONLY_BODY_50"
DESCRIPTION = "SYNTHETIC_USER_ONLY_DESCRIPTION_50"
CONTROL = "routing-control"
CONTROL_BODY = "SYNTHETIC_MODEL_CONTROL_BODY_50"
CONTROL_DESCRIPTION = "SYNTHETIC_MODEL_CONTROL_DESCRIPTION_50"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument(
        "--require-expansion", action="store_true",
        help="Fail unless CLI prompt mode expands the explicit user-only skill.",
    )
    args = parser.parse_args()
    executable = shutil.which("copilot")
    if not executable:
        raise RuntimeError("copilot is not installed")

    requests = []
    attempted_skill = None

    class CaptureHandler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            requests.append(request)
            reply = {
                "id": "local-capture",
                "object": "chat.completion",
                "created": 0,
                "model": "gpt-4.1",
                "choices": [{
                    "index": 0,
                    "message": {"role": "assistant", "content": "CAPTURE_COMPLETE"},
                    "finish_reason": "stop",
                }],
                "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
            }
            if attempted_skill and BODY not in json.dumps(request.get("messages", [])) and not any(
                message.get("role") == "tool" for message in request.get("messages", [])
            ):
                reply["choices"][0]["message"] = {
                    "role": "assistant", "content": None,
                    "tool_calls": [{
                        "id": "synthetic-skill-call", "type": "function",
                        "function": {
                            "name": "skill",
                            "arguments": json.dumps({"skill": attempted_skill}),
                        },
                    }],
                }
                reply["choices"][0]["finish_reason"] = "tool_calls"
            if request.get("stream"):
                delta = dict(reply["choices"][0]["message"])
                if "tool_calls" in delta:
                    delta["tool_calls"][0]["index"] = 0
                chunk = {
                    "id": "local-capture",
                    "object": "chat.completion.chunk",
                    "created": 0,
                    "model": "gpt-4.1",
                    "choices": [{
                        "index": 0,
                        "delta": delta,
                        "finish_reason": None,
                    }],
                }
                stop = {**chunk, "choices": [{
                    "index": 0, "delta": {},
                    "finish_reason": reply["choices"][0]["finish_reason"],
                }]}
                payload = (
                    f"data: {json.dumps(chunk)}\n\n"
                    f"data: {json.dumps(stop)}\n\n"
                    "data: [DONE]\n\n"
                ).encode()
                content_type = "text/event-stream"
            else:
                payload = json.dumps(reply).encode()
                content_type = "application/json"
            self.send_response(200)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

    with tempfile.TemporaryDirectory(prefix="skill-routing-50-") as directory:
        root = Path(directory)
        project = root / "project"
        skill = project / ".github" / "skills" / NAME
        skill.mkdir(parents=True)
        (skill / "SKILL.md").write_text(
            f"---\nname: {NAME}\ndescription: {DESCRIPTION}\n"
            "disable-model-invocation: true\n---\n\n"
            f"Reply only with {BODY}. Do not use any tools.\n",
            encoding="utf-8",
        )
        control = project / ".github" / "skills" / CONTROL
        control.mkdir()
        (control / "SKILL.md").write_text(
            f"---\nname: {CONTROL}\n"
            f"description: {CONTROL_DESCRIPTION}\n---\n\n"
            f"Reply only with {CONTROL_BODY}. Do not use any tools.\n",
            encoding="utf-8",
        )
        home = root / "home"
        home.mkdir()
        env = {
            key: value for key, value in os.environ.items()
            if not key.upper().startswith(("COPILOT_", "GITHUB_COPILOT_"))
            and key.upper() not in ("GH_TOKEN", "GITHUB_TOKEN")
        }
        env.update({
            "COPILOT_HOME": str(home / ".copilot"),
            "HOME": str(home),
            "USERPROFILE": str(home),
            "COPILOT_OFFLINE": "true",
            "COPILOT_PROVIDER_TYPE": "openai",
            "COPILOT_PROVIDER_WIRE_API": "completions",
            "COPILOT_MODEL": "gpt-4.1",
            "COPILOT_PROVIDER_MAX_PROMPT_TOKENS": "128000",
            "COPILOT_PROVIDER_MAX_OUTPUT_TOKENS": "256",
            "NO_PROXY": "127.0.0.1,localhost",
        })
        command = [executable, "--prefer-version", args.version]

        def run(arguments):
            result = subprocess.run(
                command + arguments, cwd=project, env=env,
                capture_output=True, text=True, encoding="utf-8", timeout=60,
            )
            if result.returncode:
                raise RuntimeError(
                    f"CLI exited {result.returncode}: {result.stdout}\n{result.stderr}"
                )
            return result.stdout

        version = run(["--version"]).strip().splitlines()[0]
        print(version, flush=True)
        reported_version = version.removeprefix("GitHub Copilot CLI ").removesuffix(".")
        if args.version != reported_version:
            raise RuntimeError("Requested version did not run")
        inventory = json.loads(run(["skill", "list", "--json"]))
        for name, path in ((NAME, skill), (CONTROL, control)):
            discovered = [
                item for item in inventory
                if item["name"] == name and item["enabled"]
                and Path(item["path"]).resolve() == path.resolve()
            ]
            if len(discovered) != 1:
                raise AssertionError(f"{name}: not discovered exactly once at fixture path")
        print("PASS: both synthetic skills discovered and enabled", flush=True)
        extra_sources = sorted({
            item["source"] for item in inventory
            if item["name"] not in (NAME, CONTROL)
        })
        print(f"Additional skill sources (not captured to disk): {extra_sources}", flush=True)

        server = ThreadingHTTPServer(("127.0.0.1", 0), CaptureHandler)
        env["COPILOT_PROVIDER_BASE_URL"] = f"http://127.0.0.1:{server.server_port}/v1"
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        explicit_expanded = False
        try:
            for case, prompt, target in (
                ("implicit", "Reply with READY. Do not use any skills.", None),
                ("explicit", f"/{NAME}", NAME),
                ("control", f"/{CONTROL}", CONTROL),
            ):
                requests.clear()
                attempted_skill = target
                run([
                    "--trust-folder", "--no-custom-instructions",
                    "--disable-builtin-mcps", "--disable-client-telemetry",
                    "--no-remote", "--no-remote-export", "--no-ask-user",
                    "--dynamic-retrieval=skills=off",
                    "--available-tools=skill", "--allow-tool=skill",
                    "--stream=off", "--prompt", prompt,
                ])
                model_requests = [item for item in requests if "messages" in item]
                if not model_requests:
                    raise AssertionError(f"{case}: no model request captured")
                messages = json.dumps(model_requests[0]["messages"])
                actual_body = BODY in messages
                description_visible = DESCRIPTION in messages
                control_visible = CONTROL_DESCRIPTION in messages
                tool_messages = [
                    message.get("content") for request in model_requests
                    for message in request["messages"]
                    if message.get("role") == "tool"
                ]
                if case == "implicit":
                    if actual_body or description_visible or not control_visible:
                        raise AssertionError("Implicit catalog exclusion or positive control failed")
                    print("PASS: user-only body/description hidden; control description visible", flush=True)
                elif case == "explicit":
                    explicit_expanded = actual_body
                    if actual_body:
                        print("OBSERVED: CLI prompt mode expanded the explicit user-only skill", flush=True)
                    elif any(
                        f"/{NAME}" in json.dumps(message.get("content"))
                        for message in model_requests[0]["messages"]
                        if message.get("role") == "user"
                    ) and any(f'Skill "{NAME}" not found' in str(item) for item in tool_messages):
                        print("OBSERVED: raw slash prompt; user-only model-tool call rejected", flush=True)
                    else:
                        raise AssertionError("Neither explicit expansion nor model-tool rejection observed")
                elif not (
                    any(f'Skill "{CONTROL}" loaded successfully' in str(item) for item in tool_messages)
                    and any(CONTROL_BODY in json.dumps(item["messages"]) for item in model_requests)
                ):
                    raise AssertionError("Model-invocable control did not load its actual body")
                else:
                    print("PASS: model-invocable control tool loaded its body", flush=True)
            if not explicit_expanded:
                print("LIMIT: no CLI prompt-mode explicit expansion; no desktop-route verdict", flush=True)
            if args.require_expansion and not explicit_expanded:
                raise AssertionError("Required explicit user-only expansion was not observed")
        finally:
            server.shutdown()
            server.server_close()
            worker.join()


if __name__ == "__main__":
    main()
