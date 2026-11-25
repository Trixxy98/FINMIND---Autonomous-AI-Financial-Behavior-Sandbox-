"""FINMIND demo (ADR-0017 step 6): one command from a configured checkout to a running app with sample runs.

It uses its own database, finmind_demo, and never touches any other. It creates that database with a marker
comment and will drop and recreate only a database that carries the marker. Mock agents only; no provider is called.

Steps: start PostgreSQL, (re)create finmind_demo and apply every migration, build and start the API with generated
tokens, run the sample configurations in samples/configs.json, then start the web app and print its address and the
researcher token to paste (generated for this demo, held nowhere else). Ctrl-C stops everything the demo started.

Usage (from the repository root, with .env and .env.api set up as in README.md):
  python3 services/agent-runtime/scripts/demo.py                 # demo with the web app
  python3 services/agent-runtime/scripts/demo.py --no-ui         # runs only, then stop
  python3 services/agent-runtime/scripts/demo.py --no-ui --samples samples   # also write the sample exports
"""

import argparse
import json
import os
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
RUNTIME = ROOT / "services" / "agent-runtime"
sys.path.insert(0, str(RUNTIME))
from finmind_runtime.agents import create_agent  # noqa: E402
from finmind_runtime.client import Client  # noqa: E402
from finmind_runtime.worker import Worker  # noqa: E402

DATABASE = "finmind_demo"
MARKER = "finmind-demo: created by services/agent-runtime/scripts/demo.py"
API = "http://127.0.0.1:5080"
UI = "http://127.0.0.1:5173"
API_DLL = ROOT / "apps" / "api" / "bin" / "Release" / "net10.0" / "Finmind.Api.dll"
COMPOSE = ["docker", "compose", "--env-file", str(ROOT / ".env"), "-f", str(ROOT / "infra" / "compose.yaml")]


def read_env(path):
    """KEY=VALUE lines; quotes around a value are removed. Values are never printed."""
    values = {}
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip().strip('"').strip("'")
    return values


def demo_connection(connection):
    """The API's own connection string, pointed at the demo database. Refuses anything but the restricted role."""
    if not re.search(r"(^|;)\s*Username\s*=\s*finmind_api\s*(;|$)", connection, re.IGNORECASE):
        raise SystemExit("ConnectionStrings__Finmind in .env.api must use the finmind_api role.")
    if not re.search(r"(^|;)\s*Database\s*=", connection, re.IGNORECASE):
        return connection.rstrip(";") + f";Database={DATABASE}"
    return re.sub(r"((?:^|;)\s*Database\s*=)[^;]*", rf"\g<1>{DATABASE}", connection, flags=re.IGNORECASE)


def may_drop(exists, comment):
    """Only a database this script created (it carries the marker) may be dropped."""
    return not exists or comment == MARKER


def port_busy(port):
    with socket.socket() as probe:
        return probe.connect_ex(("127.0.0.1", port)) == 0


def psql(owner, database, sql, stdin=None):
    command = COMPOSE + ["exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1", "-U", owner, "-d", database]
    command += ["-f", "-"] if stdin is not None else ["-tAc", sql]
    result = subprocess.run(command, input=stdin, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit(f"psql failed: {result.stderr.strip().splitlines()[-1] if result.stderr.strip() else 'no output'}")
    return result.stdout.strip()


def prepare_database(owner):
    exists = psql(owner, "postgres", f"SELECT 1 FROM pg_database WHERE datname = '{DATABASE}'") == "1"
    comment = psql(owner, "postgres", f"SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = '{DATABASE}'") \
        if exists else None
    if not may_drop(exists, comment):
        raise SystemExit(f"A database named {DATABASE} exists that this script did not create. Rename or remove it yourself.")
    if exists:
        psql(owner, "postgres", f"DROP DATABASE {DATABASE} WITH (FORCE)")
    psql(owner, "postgres", f"CREATE DATABASE {DATABASE}")
    psql(owner, "postgres", f"COMMENT ON DATABASE {DATABASE} IS '{MARKER}'")
    migrations = sorted((ROOT / "infra" / "db" / "migrations").glob("*.sql"))
    for migration in migrations:
        psql(owner, DATABASE, None, stdin=migration.read_text(encoding="utf-8"))
    return len(migrations)


class Demo:
    def __init__(self):
        self.worker_token = secrets.token_hex(24)
        self.researcher_token = secrets.token_hex(24)
        self.processes = []

    def start(self, command, cwd, env, log):
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=open(log, "ab"), stderr=subprocess.STDOUT,
                                   start_new_session=True)
        self.processes.append(process)
        return process

    def stop(self):
        for process in reversed(self.processes):
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)

    def call(self, method, path, payload=None, raw=False):
        data = None if payload is None else json.dumps(payload).encode()
        request = urllib.request.Request(API + "/api/v1" + path, data=data, method=method, headers={
            "Content-Type": "application/json", "Authorization": "Bearer " + self.researcher_token})
        with urllib.request.urlopen(request, timeout=60) as response:
            body = response.read()
            return body if raw else json.loads(body or b"null")

    def wait_for(self, url, seconds, what):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                with urllib.request.urlopen(url, timeout=2):
                    return
            except OSError:
                time.sleep(0.5)
        raise SystemExit(f"{what} did not start in {seconds} s; see the log in {self.log_dir}.")

    def run_samples(self, samples):
        runs = []
        for sample in samples:
            run = self.call("POST", "/runs", sample["config"])["runId"]
            self.call("POST", f"/runs/{run}/start")
            runs.append((sample["name"], run))

        def mock_only(snapshot, tools):
            if snapshot["agent"]["provider"] != "mock":
                raise ValueError("the demo runs mock agents only")
            return create_agent(snapshot, tools)
        worker = Worker(Client(API, self.worker_token), agent_factory=mock_only)
        while worker.run_once() is not None:
            pass
        return runs


def main(argv=None):
    parser = argparse.ArgumentParser(description="FINMIND demo with mock agents and its own database.")
    parser.add_argument("--no-ui", action="store_true", help="run the samples, print a summary, and stop")
    parser.add_argument("--samples", metavar="DIR", help="write each run's JSONL and CSV export to DIR")
    args = parser.parse_args(argv)

    for name in (".env", ".env.api"):
        if not (ROOT / name).is_file():
            raise SystemExit(f"{name} is missing. Set up the environment files as in README.md first.")
    busy = [port for port in ((5080,) if args.no_ui else (5080, 5173)) if port_busy(port)]
    if busy:
        raise SystemExit(f"Port(s) {busy} in use. Stop the API or web app you are running, then try again.")

    owner = read_env(ROOT / ".env").get("POSTGRES_USER", "finmind")
    connection = demo_connection(read_env(ROOT / ".env.api")["ConnectionStrings__Finmind"])
    samples = json.loads((ROOT / "samples" / "configs.json").read_text(encoding="utf-8"))["runs"]

    demo = Demo()
    demo.log_dir = Path(os.environ.get("FINMIND_DEMO_LOG_DIR") or tempfile.mkdtemp(prefix="finmind-demo-"))
    demo.log_dir.mkdir(parents=True, exist_ok=True)
    try:
        print("Starting PostgreSQL…", flush=True)
        subprocess.run(COMPOSE + ["up", "-d", "--wait"], check=True, capture_output=True)
        print(f"Creating {DATABASE} and applying migrations…", flush=True)
        print(f"  {prepare_database(owner)} migrations applied", flush=True)
        print("Building the API…", flush=True)
        subprocess.run(["dotnet", "build", str(ROOT / "apps/api/Finmind.Api.csproj"), "-c", "Release", "-v", "quiet"],
                       check=True, capture_output=True)
        revision = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
        demo.start(["dotnet", str(API_DLL)], ROOT, {
            "PATH": os.environ["PATH"], "HOME": os.environ.get("HOME", "/tmp"), "ConnectionStrings__Finmind": connection,
            "FINMIND_WORKER_TOKEN": demo.worker_token, "FINMIND_RESEARCHER_TOKEN": demo.researcher_token,
            "FINMIND_CODE_REVISION": revision}, demo.log_dir / "api.log")
        demo.wait_for(API + "/health", 60, "The API")

        print(f"Running {len(samples)} sample runs with mock agents…", flush=True)
        runs = demo.run_samples(samples)
        for name, run in runs:
            detail = demo.call("GET", f"/runs/{run}")
            wallets = demo.call("GET", f"/runs/{run}/wallets")
            cash = next(a["balanceSen"] for a in wallets["accounts"] if a["kind"] == "cash")
            intact = demo.call("GET", f"/runs/{run}/verify")["intact"]
            print(f"  {name}: {detail['status']}, {detail['actionCounts']['total']} actions, "
                  f"cash MYR {cash / 100:,.2f}, chain {'intact' if intact else 'BROKEN'}, reconciled {wallets['reconciled']}")
            if args.samples:
                out = Path(args.samples)
                out.mkdir(parents=True, exist_ok=True)
                (out / f"{name}.jsonl").write_bytes(demo.call("GET", f"/runs/{run}/export?format=jsonl", raw=True))
                (out / f"{name}.csv").write_bytes(demo.call("GET", f"/runs/{run}/export?format=csv", raw=True))
        if args.samples:
            print(f"  exports written to {args.samples}", flush=True)

        if args.no_ui:
            return 0
        web = ROOT / "apps" / "web"
        if not (web / "node_modules").is_dir():
            print("Installing web dependencies (npm ci)…", flush=True)
            subprocess.run(["npm", "ci"], cwd=web, check=True, capture_output=True)
        demo.start([shutil.which("npx") or "npx", "vite"], web, dict(os.environ), demo.log_dir / "web.log")
        demo.wait_for(UI, 60, "The web app")
        print(f"\nOpen {UI} and paste this researcher token (valid for this demo only):\n\n  {demo.researcher_token}\n")
        print(f"Logs: {demo.log_dir}. Press Ctrl-C to stop the demo.", flush=True)
        signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        pass
    finally:
        demo.stop()
        print("Demo stopped. PostgreSQL keeps running; finmind_demo stays until the next demo.", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
