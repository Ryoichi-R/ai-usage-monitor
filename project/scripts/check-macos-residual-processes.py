"""List processes that AI Usage Monitor for macOS started and that are still alive.

Used in the Mac acceptance termination cases (normal quit, force quit, timeout).
Read-only: it never signals a process. Only the current user's processes are
inspected. Three sources are combined:
- the monitor and supervisor executables by name;
- the supervisor journals (PID and start time of every tracked process), which
  remain only while cleanup has not been proven;
- the supervisor's tracking token name in a process's exec-time environment,
  read with the same KERN_PROCARGS2 call as the supervisor, matched in memory
  and never printed. macOS 27 does not return the environment of Apple platform
  binaries (for example /bin/sh or git), so the journal is the primary source.
Output is limited to PID and executable name.
Exit code: 0 when nothing remains, 1 when a residual process is found.
"""
import ctypes
import os
import subprocess
import sys
import time
from pathlib import Path

TOKEN_NAME = b"AIUSAGE_TRACK_TOKEN="
OWN_NAMES = ("AiUsageMonitor.App.Mac", "ai-usage-process-supervisor")
CTL_KERN, KERN_ARGMAX, KERN_PROCARGS2 = 1, 8, 49
libc = ctypes.CDLL("/usr/lib/libc.dylib", use_errno=True)


def sysctl(mib, buffer, size):
    array = (ctypes.c_int * len(mib))(*mib)
    return libc.sysctl(array, len(mib), buffer, ctypes.byref(size), None, 0) == 0


def argmax():
    value, size = ctypes.c_int(0), ctypes.c_size_t(ctypes.sizeof(ctypes.c_int))
    return value.value if sysctl([CTL_KERN, KERN_ARGMAX], ctypes.byref(value), size) else 0


def has_token(pid, limit):
    # Layout: argc, exec path, NUL padding, argc argument strings, then environment strings.
    buffer, size = ctypes.create_string_buffer(limit), ctypes.c_size_t(limit)
    if not sysctl([CTL_KERN, KERN_PROCARGS2, pid], buffer, size) or size.value < 4:
        return False
    data = buffer.raw[:size.value]
    ctypes.memset(buffer, 0, limit)
    argc = int.from_bytes(data[:4], sys.byteorder)
    position = data.find(b"\0", 4)
    while 0 <= position < len(data) and data[position] == 0:
        position += 1
    strings = data[position:].split(b"\0") if position >= 0 else []
    return any(entry.startswith(TOKEN_NAME) for entry in strings[argc:])


def journal_targets(root):
    targets = {}
    for journal in Path(root).glob("*/processes"):
        for line in journal.read_text(encoding="ascii", errors="ignore").splitlines():
            parts = line.split()
            if len(parts) == 2 and parts[0].isdigit() and parts[1].isdigit():
                targets[int(parts[0])] = int(parts[1]) // 1_000_000
    return targets


def started_at(pid):
    # Seconds resolution is enough to reject a recycled PID with a different start time.
    result = subprocess.run(["/bin/ps", "-p", str(pid), "-o", "lstart="], capture_output=True, text=True,
                            env={**os.environ, "LC_ALL": "C"})
    text = result.stdout.strip()
    return int(time.mktime(time.strptime(text, "%a %b %d %H:%M:%S %Y"))) if text else None


def main():
    # Optional argument: the ProcessSessions directory (default: the product location).
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.home() / "Library" / "Application Support" / "AiUsageMonitor" / "ProcessSessions"
    journal = journal_targets(root) if root.is_dir() else {}
    result = subprocess.run(["/bin/ps", "-U", str(os.getuid()), "-o", "pid=,comm="],
                            capture_output=True, text=True, check=True)
    limit, found = argmax(), []
    for line in result.stdout.splitlines():
        pid_text, _, comm = line.strip().partition(" ")
        if not pid_text.isdigit() or int(pid_text) == os.getpid():
            continue
        pid, name = int(pid_text), os.path.basename(comm.strip())
        if name in OWN_NAMES or comm.strip().endswith(OWN_NAMES):
            found.append((pid, name, "monitor or supervisor"))
        elif pid in journal and started_at(pid) is not None and abs(started_at(pid) - journal[pid]) <= 1:
            found.append((pid, name, "journaled CLI or descendant"))
        elif limit and has_token(pid, limit):
            found.append((pid, name, "tracked CLI or descendant"))
    for pid, name, kind in found:
        print(f"{pid}\t{name}\t{kind}")
    print(f"residual={len(found)}")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
