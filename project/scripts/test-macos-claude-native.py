"""Security boundaries exercised only with signed synthetic executables and private sockets."""
import argparse
import ctypes
import os
from pathlib import Path
import socket
import subprocess
import tempfile


def run(*args, **kwargs):
    return subprocess.run(args, capture_output=True, timeout=15, **kwargs)


def launch(helper, executable, root, replacement=None):
    root.mkdir(mode=0o700)
    journal = root / "journal"
    journal.mkdir(mode=0o700)
    marker = root / "marker"
    with socket.socket(socket.AF_UNIX) as listener:
        listener.bind(str(root / "control"))
        listener.listen(1)
        listener.settimeout(5)
        environment = dict(os.environ)
        if replacement:
            environment["AIUSAGE_TEST_LAUNCH_PATH"] = str(replacement)
        process = subprocess.Popen([str(helper), "--claude", str(journal), str(root / "control"),
                                    str(executable), "--marker", str(marker)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=environment)
        try:
            peer, _ = listener.accept()
            with peer:
                peer.settimeout(5)
                ready = peer.recv(1)
                output, error = process.communicate(timeout=10)
                assert not output and not error
                assert marker.exists() == (replacement is None), "unverified fake executed"
                assert (ready == b"R") == (replacement is None)
                assert process.returncode == (0 if replacement is None else 77)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=3)
            assert run(str(helper), "--sweep", str(journal)).returncode == 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--production", required=True)
    parser.add_argument("--test-helper", required=True)
    parser.add_argument("--library", required=True)
    parser.add_argument("--fake-source", required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="aiusage-sign-", dir="/private/tmp") as folder:
        root = Path(folder)
        first, second = root / "first", root / "second"
        variant = root / "variant.c"
        variant.write_text(Path(args.fake_source).read_text() + "\nint fake_variant = 1;\n")
        for source, binary in [(args.fake_source, first), (str(variant), second)]:
            assert run("clang", source, "-o", str(binary)).returncode == 0
            assert run("/usr/bin/codesign", "--force", "--sign", "-", "--identifier", "org.example.ai-usage-suspended-probe", str(binary)).returncode == 0
        assert run(args.production, "--verify-claude", str(first)).returncode == 77
        assert run(args.test_helper, "--verify-claude", str(first)).returncode == 0
        assert run(args.test_helper, "--verify-claude", str(root / "absent")).returncode == 77
        launch(args.test_helper, first, root / "valid")
        launch(args.test_helper, first, root / "mismatch", second)
        launch(args.test_helper, first, root / "untrusted", Path("/bin/echo"))
        print("static + suspended dynamic signature + unique identity mismatch: PASS", flush=True)
        lib = ctypes.CDLL(args.library)
        validate = lib.aiusage_validate
        validate.argtypes = [ctypes.c_char_p, ctypes.c_int]
        lock = lib.aiusage_lock_directory
        lock.argtypes = [ctypes.c_char_p]
        assert validate(os.fsencode(root), 1) == 0
        assert validate(os.fsencode(root / "absent"), 0) == 2
        assert validate(os.fsencode(first), 0) == 1
        root.chmod(0o755)
        assert lock(os.fsencode(root)) == -1
        root.chmod(0o700)
        fd = lock(os.fsencode(root))
        assert fd >= 0
        assert lock(os.fsencode(root)) == -1
        os.close(fd)
        assert lib.aiusage_peer(-1) == 1
        left, right = socket.socketpair()
        try:
            assert lib.aiusage_peer(left.fileno()) == 0
        finally:
            left.close(); right.close()
        link = root / "link"
        link.symlink_to(first)
        assert validate(os.fsencode(link), 0) == 1
        # Kernel credential seam: exercise a valid getpeereid result belonging to a different UID.
        # This does not require switching the test runner to another account.
        import json
        socket_source = Path(__file__).resolve().parents[1] / "src/AiUsageMonitor.Platform.Mac/Native/local-socket.c"
        mock_source, mock_library = root / "peer-mismatch.c", root / "peer-mismatch.dylib"
        mock_source.write_text("#define getpeereid aiusage_mock_getpeereid\n#include " + json.dumps(str(socket_source)) +
                              "\nint aiusage_mock_getpeereid(int fd, uid_t *uid, gid_t *gid) { (void)fd; *uid=getuid()+1; *gid=getgid(); return 0; }\n")
        assert run("clang", "-dynamiclib", str(mock_source), "-o", str(mock_library)).returncode == 0
        assert ctypes.CDLL(str(mock_library)).aiusage_peer(0) == 1
        print("socket metadata + directory lock + peer credentials: PASS", flush=True)


if __name__ == "__main__":
    main()
