"""Production supervisor acceptance with fake processes only; no real CLI or credentials."""
import argparse
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time


def wait_for(predicate, seconds=10):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if predicate():
            return
        time.sleep(.02)
    raise AssertionError("deadline exceeded")


def alive(pid):
    value = subprocess.run(["/bin/ps", "-p", str(pid), "-o", "stat="], capture_output=True, text=True).stdout.strip()
    return bool(value) and not value.startswith("Z")


def fake(root, escape):
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    signal.signal(signal.SIGHUP, signal.SIG_IGN)
    child = os.fork()
    if child == 0:
        if escape:
            os.setsid()
        while True:
            time.sleep(.2)
    (root / "truth.json").write_text(json.dumps([os.getpid(), child]))
    while True:
        time.sleep(.2)


def owner(helper, root, escape):
    journal = root / "journal"
    journal.mkdir(mode=0o700)
    listener = socket.socket(socket.AF_UNIX)
    listener.bind(str(root / "control"))
    listener.listen(1)
    args = [helper, "--run", str(journal), str(root / "control"), sys.executable,
            str(Path(__file__).resolve()), "--fake", str(root)]
    if escape:
        args.append("--escape")
    process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    (root / "helper.json").write_text(json.dumps(process.pid))
    control, _ = listener.accept()
    assert control.recv(1) == b"R"
    (root / "ready").touch()
    while True:
        time.sleep(.2)


def sweep(helper, journal):
    result = subprocess.run([helper, "--sweep", str(journal)], timeout=9, capture_output=True)
    assert result.returncode == 0, "sweep failed"


def trial(helper, mode):
    # Short AF_UNIX path, private directory; only these fake identities are terminated.
    with tempfile.TemporaryDirectory(prefix="aiusage-test-", dir="/tmp") as directory:
        root = Path(directory)
        args = [sys.executable, str(Path(__file__).resolve()), "--owner", helper, str(root)]
        if mode == "escape":
            args.append("--escape")
        app = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        pids = []
        helper_pid = None
        try:
            wait_for(lambda: (root / "ready").exists() and (root / "truth.json").exists())
            pids = json.loads((root / "truth.json").read_text())
            helper_pid = json.loads((root / "helper.json").read_text())
            if mode == "parent-kill":
                app.kill()
                app.wait(timeout=2)
            elif mode == "helper-kill":
                os.kill(helper_pid, signal.SIGKILL)
                wait_for(lambda: not alive(helper_pid))
                sweep(helper, root / "journal")
            elif mode == "both-kill":
                os.kill(helper_pid, signal.SIGKILL)
                app.kill()
                app.wait(timeout=2)
                wait_for(lambda: not alive(helper_pid))
                sweep(helper, root / "journal")
            elif mode == "escape":
                pass  # supervisor must detect and terminate without owner intervention
            wait_for(lambda: all(not alive(pid) for pid in pids), 8)
            wait_for(lambda: not alive(helper_pid), 3)
            sweep(helper, root / "journal")
        finally:
            if app.poll() is None:
                app.kill()
            app.wait(timeout=3)
            if helper_pid and alive(helper_pid):
                os.kill(helper_pid, signal.SIGKILL)
            for pid in pids:
                if alive(pid):
                    os.kill(pid, signal.SIGKILL)


def negative_sweep(helper):
    with tempfile.TemporaryDirectory(prefix="aiusage-negative-", dir="/tmp") as directory:
        root = Path(directory)
        sentinel = subprocess.Popen(["/bin/sleep", "30"])
        try:
            journal = root / "processes"
            journal.write_text(str(sentinel.pid) + " 1\n")
            journal.chmod(0o600)
            sweep(helper, root)
            assert sentinel.poll() is None, "identity mismatch killed sentinel"
            # Invalid permissions and symlinked session metadata are rejected, never treated as an active session.
            root.chmod(0o777)
            rejected = subprocess.run([helper, "--sweep", str(root)], capture_output=True, timeout=3)
            assert rejected.returncode == 77
            root.chmod(0o700)
            target = root / "not-metadata"
            target.write_text("fixture")
            (root / "session").symlink_to(target)
            rejected = subprocess.run([helper, "--sweep", str(root)], capture_output=True, timeout=3)
            assert rejected.returncode == 77
        finally:
            sentinel.terminate()
            sentinel.wait(timeout=3)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--helper")
    parser.add_argument("--fake")
    parser.add_argument("--owner", nargs=2)
    parser.add_argument("--escape", action="store_true")
    parser.add_argument("--repeat", type=int, default=2)
    args = parser.parse_args()
    if args.fake:
        return fake(Path(args.fake), args.escape)
    if args.owner:
        return owner(args.owner[0], Path(args.owner[1]), args.escape)
    for mode in ["parent-kill", "helper-kill", "both-kill", "escape"]:
        for _ in range(args.repeat):
            trial(args.helper, mode)
        print(mode + ": PASS", flush=True)
    negative_sweep(args.helper)
    print("identity-mismatch: PASS", flush=True)


if __name__ == "__main__":
    main()
