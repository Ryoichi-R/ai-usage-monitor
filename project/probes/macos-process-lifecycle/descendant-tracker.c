// P0-9 follow-up probe: non-cooperative descendant tracking, identity-verified
// cleanup, helper session separation, next-launch journal sweep, and an
// observation mode for a signature-verified CLI.
//
// The self-test uses fake CLIs only. `--observe` launches the given executable
// only after a strict Security.framework requirement check on an opened FD and a
// dynamic check of the stopped process. It never prints terminal contents.
#define _DARWIN_C_SOURCE
#include <CoreFoundation/CoreFoundation.h>
#include <Security/Security.h>
#include <errno.h>
#include <fcntl.h>
#include <libproc.h>
#include <mach-o/dyld.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <spawn.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/event.h>
#include <sys/ioctl.h>
#include <sys/proc.h>
#include <sys/stat.h>
#include <sys/sysctl.h>
#include <sys/time.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>
#include <util.h>

#define MAX_TRACKED 1024
#define MAX_NEGATIVE_CACHE 256
#define TOKEN_NAME "AIUSAGE_TRACK_TOKEN="

static char self_path[PATH_MAX];

static void die(const char *message)
{
    perror(message);
    exit(2);
}

static int64_t now_ms(void)
{
    struct timespec value;
    clock_gettime(CLOCK_MONOTONIC, &value);
    return (int64_t)value.tv_sec * 1000 + value.tv_nsec / 1000000;
}

static uint64_t wall_us(void)
{
    struct timeval value;
    gettimeofday(&value, NULL);
    return (uint64_t)value.tv_sec * 1000000ULL + (uint64_t)value.tv_usec;
}

static void sleep_ms(int milliseconds)
{
    struct timespec value = { milliseconds / 1000, (long)(milliseconds % 1000) * 1000000L };
    while (nanosleep(&value, &value) != 0 && errno == EINTR) { }
}

// ---------------------------------------------------------------------------
// Process identity
// ---------------------------------------------------------------------------

typedef struct {
    bool ok;
    pid_t ppid;
    pid_t pgid;
    uid_t uid;
    uint64_t start_us;
    uint32_t status;
    char comm[MAXCOMLEN + 1];
} snapshot_t;

static snapshot_t snapshot(pid_t pid)
{
    snapshot_t result;
    memset(&result, 0, sizeof(result));
    struct proc_bsdinfo info;
    if (pid <= 0 || proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info)) != (int)sizeof(info)) return result;
    result.ok = true;
    result.ppid = (pid_t)info.pbi_ppid;
    result.pgid = (pid_t)info.pbi_pgid;
    result.uid = info.pbi_uid;
    result.start_us = (uint64_t)info.pbi_start_tvsec * 1000000ULL + (uint64_t)info.pbi_start_tvusec;
    result.status = info.pbi_status;
    memcpy(result.comm, info.pbi_comm, sizeof(result.comm) - 1);
    return result;
}

static bool live(snapshot_t value) { return value.ok && value.status != SZOMB; }

// Same pid AND same start time: guards every signal against PID reuse.
static bool same_live_process(pid_t pid, uint64_t start_us)
{
    snapshot_t value = snapshot(pid);
    return live(value) && value.start_us == start_us;
}

static bool environment_has_token(pid_t pid, const char *token)
{
    int argmax_mib[2] = { CTL_KERN, KERN_ARGMAX };
    int argmax = 0;
    size_t size = sizeof(argmax);
    if (sysctl(argmax_mib, 2, &argmax, &size, NULL, 0) != 0 || argmax <= 0) return false;
    char *buffer = malloc((size_t)argmax);
    if (buffer == NULL) return false;
    int mib[3] = { CTL_KERN, KERN_PROCARGS2, pid };
    size = (size_t)argmax;
    bool found = false;
    if (sysctl(mib, 3, buffer, &size, NULL, 0) == 0)
        found = memmem(buffer, size, token, strlen(token)) != NULL;
    // Arguments and environment of candidates are compared in memory only.
    memset(buffer, 0, size);
    free(buffer);
    return found;
}

// ---------------------------------------------------------------------------
// Descendant tracker
// ---------------------------------------------------------------------------

typedef struct {
    pid_t pid;
    uint64_t start_us;
    pid_t pgid;
    pid_t sid;
    bool alive;
    bool escaped;
    bool from_orphan_scan;
    char comm[MAXCOMLEN + 1];
} tracked_t;

typedef struct {
    tracked_t items[MAX_TRACKED];
    int count;
    pid_t root;
    pid_t root_pgid;
    pid_t root_sid;
    bool root_reaped;
    int root_status;
    uint64_t session_start_us;
    int kq;
    char token[64];
    const char *journal_path;
    int escapes;
    int orphan_hits;
    int overflow;
    int max_alive;
    struct { pid_t pid; uint64_t start_us; } negative[MAX_NEGATIVE_CACHE];
    int negative_count;
} tracker_t;

static void tracker_init(tracker_t *tracker, const char *journal_path)
{
    memset(tracker, 0, sizeof(*tracker));
    tracker->kq = kqueue();
    if (tracker->kq < 0) die("kqueue");
    (void)fcntl(tracker->kq, F_SETFD, FD_CLOEXEC);
    unsigned char random_bytes[16];
    arc4random_buf(random_bytes, sizeof(random_bytes));
    int offset = snprintf(tracker->token, sizeof(tracker->token), "%s", TOKEN_NAME);
    for (size_t index = 0; index < sizeof(random_bytes); index++)
        offset += snprintf(tracker->token + offset, sizeof(tracker->token) - (size_t)offset, "%02x", random_bytes[index]);
    tracker->journal_path = journal_path;
    tracker->session_start_us = wall_us() - 1000;
}

static tracked_t *tracker_find_pid(tracker_t *tracker, pid_t pid)
{
    for (int index = 0; index < tracker->count; index++)
        if (tracker->items[index].pid == pid && tracker->items[index].alive) return &tracker->items[index];
    return NULL;
}

static void tracker_write_journal(tracker_t *tracker)
{
    if (tracker->journal_path == NULL) return;
    char temporary[PATH_MAX];
    (void)snprintf(temporary, sizeof(temporary), "%s.tmp", tracker->journal_path);
    int fd = open(temporary, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    if (fd < 0) return;
    for (int index = 0; index < tracker->count; index++) {
        char line[96];
        int length = snprintf(line, sizeof(line), "%d %llu\n", tracker->items[index].pid,
                              (unsigned long long)tracker->items[index].start_us);
        if (write(fd, line, (size_t)length) != length) break;
    }
    close(fd);
    (void)rename(temporary, tracker->journal_path);
}

static void tracker_check_escape(tracker_t *tracker, tracked_t *item)
{
    if (item->escaped) return;
    if (item->pgid != tracker->root_pgid || item->sid != tracker->root_sid) {
        item->escaped = true;
        tracker->escapes++;
    }
}

static bool tracker_add(tracker_t *tracker, pid_t pid, bool from_orphan_scan)
{
    if (tracker_find_pid(tracker, pid) != NULL) return false;
    snapshot_t value = snapshot(pid);
    if (!live(value)) return false;
    if (tracker->count >= MAX_TRACKED) { tracker->overflow++; return false; }
    tracked_t *item = &tracker->items[tracker->count++];
    memset(item, 0, sizeof(*item));
    item->pid = pid;
    item->start_us = value.start_us;
    item->pgid = value.pgid;
    item->sid = getsid(pid);
    item->alive = true;
    item->from_orphan_scan = from_orphan_scan;
    memcpy(item->comm, value.comm, sizeof(item->comm));
    struct kevent change;
    EV_SET(&change, (uintptr_t)pid, EVFILT_PROC, EV_ADD | EV_CLEAR, NOTE_FORK | NOTE_EXEC | NOTE_EXIT, 0, NULL);
    if (kevent(tracker->kq, &change, 1, NULL, 0, NULL) != 0 && errno == ESRCH) item->alive = false;
    tracker_check_escape(tracker, item);
    tracker_write_journal(tracker);
    return true;
}

static void tracker_set_root(tracker_t *tracker, pid_t root)
{
    snapshot_t value = snapshot(root);
    if (!value.ok) die("root snapshot");
    tracker->root = root;
    tracker->root_pgid = value.pgid;
    tracker->root_sid = getsid(root);
    if (!tracker_add(tracker, root, false)) die("root registration");
}

static bool tracker_negative_cached(tracker_t *tracker, pid_t pid, uint64_t start_us)
{
    for (int index = 0; index < tracker->negative_count; index++)
        if (tracker->negative[index].pid == pid && tracker->negative[index].start_us == start_us) return true;
    return false;
}

// Orphans are re-parented to launchd (ppid 1) and drop out of the tree. A process
// is attributed to this session only when it belongs to this uid, started after
// the session began, and still carries the session token in its exec-time
// environment. Processes that exec with a scrubbed environment are not found.
static void tracker_orphan_scan(tracker_t *tracker)
{
    static pid_t all[16384];
    int bytes = proc_listpids(PROC_ALL_PIDS, 0, all, (int)sizeof(all));
    if (bytes <= 0) return;
    int total = bytes / (int)sizeof(pid_t);
    uid_t uid = getuid();
    for (int index = 0; index < total; index++) {
        pid_t pid = all[index];
        if (pid <= 1 || tracker_find_pid(tracker, pid) != NULL) continue;
        snapshot_t value = snapshot(pid);
        if (!live(value) || value.ppid != 1 || value.uid != uid || value.start_us < tracker->session_start_us) continue;
        if (tracker_negative_cached(tracker, pid, value.start_us)) continue;
        if (environment_has_token(pid, tracker->token)) {
            if (tracker_add(tracker, pid, true)) tracker->orphan_hits++;
        } else if (tracker->negative_count < MAX_NEGATIVE_CACHE) {
            tracker->negative[tracker->negative_count].pid = pid;
            tracker->negative[tracker->negative_count].start_us = value.start_us;
            tracker->negative_count++;
        }
    }
}

static int tracker_alive(tracker_t *tracker)
{
    int alive = 0;
    for (int index = 0; index < tracker->count; index++)
        if (tracker->items[index].alive) alive++;
    return alive;
}

static void tracker_refresh(tracker_t *tracker)
{
    bool added = true;
    for (int round = 0; added && round < 16; round++) {
        added = false;
        for (int index = 0; index < tracker->count; index++) {
            tracked_t *item = &tracker->items[index];
            if (!item->alive) continue;
            snapshot_t value = snapshot(item->pid);
            if (!live(value) || value.start_us != item->start_us) { item->alive = false; continue; }
            item->pgid = value.pgid;
            pid_t sid = getsid(item->pid);
            if (sid > 0) item->sid = sid;
            tracker_check_escape(tracker, item);
            pid_t children[1024];
            int bytes = proc_listpids(PROC_PPID_ONLY, (uint32_t)item->pid, children, (int)sizeof(children));
            for (int child = 0; bytes > 0 && child < bytes / (int)sizeof(pid_t); child++)
                if (children[child] > 0 && tracker_add(tracker, children[child], false)) added = true;
        }
    }
    tracker_orphan_scan(tracker);
    int alive = tracker_alive(tracker);
    if (alive > tracker->max_alive) tracker->max_alive = alive;
}

static void tracker_reap_root(tracker_t *tracker)
{
    if (tracker->root_reaped || tracker->root <= 0) return;
    int status = 0;
    pid_t waited = waitpid(tracker->root, &status, WNOHANG);
    if (waited == tracker->root) {
        tracker->root_reaped = true;
        tracker->root_status = status;
    }
}

static void tracker_pump(tracker_t *tracker, int timeout_ms)
{
    struct kevent events[64];
    struct timespec timeout = { timeout_ms / 1000, (long)(timeout_ms % 1000) * 1000000L };
    int count = kevent(tracker->kq, NULL, 0, events, 64, &timeout);
    for (int index = 0; index < count; index++) {
        if ((events[index].fflags & NOTE_EXIT) == 0) continue;
        tracked_t *item = tracker_find_pid(tracker, (pid_t)events[index].ident);
        if (item != NULL) item->alive = false;
    }
    tracker_reap_root(tracker);
    tracker_refresh(tracker);
}

static void tracker_signal_all(tracker_t *tracker, int signal_number)
{
    bool group_verified = false;
    for (int index = 0; index < tracker->count; index++) {
        tracked_t *item = &tracker->items[index];
        if (!item->alive) continue;
        snapshot_t value = snapshot(item->pid);
        if (!live(value) || value.start_us != item->start_us) { item->alive = false; continue; }
        if (value.pgid == tracker->root_pgid) group_verified = true;
        (void)kill(item->pid, signal_number);
    }
    // killpg only while a verified member proves the group still exists, so the
    // PGID cannot have been recycled for an unrelated group.
    if (group_verified) (void)killpg(tracker->root_pgid, signal_number);
}

// Returns the number of tracked processes still alive after the hard deadline.
static int tracker_terminate(tracker_t *tracker, int grace_ms, int hard_ms, bool *term_sufficient)
{
    tracker_refresh(tracker);
    tracker_signal_all(tracker, SIGTERM);
    int64_t deadline = now_ms() + grace_ms;
    while (tracker_alive(tracker) > 0 && now_ms() < deadline) tracker_pump(tracker, 20);
    if (term_sufficient != NULL) *term_sufficient = tracker_alive(tracker) == 0;
    deadline = now_ms() + hard_ms;
    while (tracker_alive(tracker) > 0 && now_ms() < deadline) {
        tracker_signal_all(tracker, SIGKILL);
        tracker_pump(tracker, 20);
    }
    for (int attempt = 0; attempt < 50 && !tracker->root_reaped; attempt++) {
        tracker_reap_root(tracker);
        if (!tracker->root_reaped) sleep_ms(10);
    }
    return tracker_alive(tracker);
}

// ---------------------------------------------------------------------------
// Journal sweep used on the next launch after both owners died.
// ---------------------------------------------------------------------------

typedef struct { int killed; int skipped_identity; int gone; int residual; } sweep_t;

static sweep_t sweep_journal(const char *path)
{
    sweep_t result = { 0, 0, 0, 0 };
    FILE *file = fopen(path, "r");
    if (file == NULL) return result;
    struct { pid_t pid; uint64_t start_us; } targets[MAX_TRACKED];
    int target_count = 0;
    int pid;
    unsigned long long start;
    while (fscanf(file, "%d %llu", &pid, &start) == 2) {
        snapshot_t value = snapshot((pid_t)pid);
        if (!live(value)) { result.gone++; continue; }
        if (value.start_us != (uint64_t)start || value.uid != getuid()) { result.skipped_identity++; continue; }
        if (kill((pid_t)pid, SIGKILL) == 0 && target_count < MAX_TRACKED) {
            targets[target_count].pid = (pid_t)pid;
            targets[target_count].start_us = (uint64_t)start;
            target_count++;
            result.killed++;
        }
    }
    fclose(file);
    int64_t deadline = now_ms() + 2000;
    for (;;) {
        result.residual = 0;
        for (int index = 0; index < target_count; index++)
            if (same_live_process(targets[index].pid, targets[index].start_us)) result.residual++;
        if (result.residual == 0 || now_ms() >= deadline) break;
        sleep_ms(20);
    }
    return result;
}

// ---------------------------------------------------------------------------
// Code signing (observe mode)
// ---------------------------------------------------------------------------

typedef struct {
    SecRequirementRef requirement;
    CFDataRef unique;
} verifier_t;

static CFDataRef copy_unique(SecStaticCodeRef code)
{
    CFDictionaryRef information = NULL;
    if (SecCodeCopySigningInformation(code, kSecCSSigningInformation, &information) != errSecSuccess || information == NULL)
        return NULL;
    CFDataRef unique = CFDictionaryGetValue(information, kSecCodeInfoUnique);
    if (unique != NULL) CFRetain(unique);
    CFRelease(information);
    return unique;
}

static OSStatus verify_static_fd(int fd, verifier_t *verifier)
{
    char path[64];
    (void)snprintf(path, sizeof(path), "/dev/fd/%d", fd);
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(NULL, (const UInt8 *)path, (CFIndex)strlen(path), false);
    SecStaticCodeRef code = NULL;
    OSStatus status = SecStaticCodeCreateWithPath(url, kSecCSDefaultFlags, &code);
    CFRelease(url);
    if (status != errSecSuccess) return status;
    status = SecStaticCodeCheckValidity(code, kSecCSStrictValidate, verifier->requirement);
    if (status == errSecSuccess) {
        verifier->unique = copy_unique(code);
        if (verifier->unique == NULL) status = errSecCSInternalError;
    }
    CFRelease(code);
    return status;
}

static OSStatus verify_dynamic_pid(pid_t pid, const verifier_t *verifier)
{
    int pid_value = (int)pid;
    CFNumberRef number = CFNumberCreate(NULL, kCFNumberIntType, &pid_value);
    const void *keys[] = { kSecGuestAttributePid };
    const void *values[] = { number };
    CFDictionaryRef attributes = CFDictionaryCreate(NULL, keys, values, 1, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFRelease(number);
    SecCodeRef guest = NULL;
    OSStatus status = SecCodeCopyGuestWithAttributes(NULL, attributes, kSecCSDefaultFlags, &guest);
    CFRelease(attributes);
    if (status != errSecSuccess) return status;
    status = SecCodeCheckValidity(guest, kSecCSStrictValidate, verifier->requirement);
    if (status == errSecSuccess) {
        SecStaticCodeRef running = NULL;
        status = SecCodeCopyStaticCode(guest, kSecCSDefaultFlags, &running);
        if (status == errSecSuccess) {
            CFDataRef unique = copy_unique(running);
            if (unique == NULL || !CFEqual(unique, verifier->unique)) status = errSecCSReqFailed;
            if (unique != NULL) CFRelease(unique);
            CFRelease(running);
        }
    }
    CFRelease(guest);
    return status;
}

// ---------------------------------------------------------------------------
// Launch: suspended spawn, tracking registered before user code runs.
// ---------------------------------------------------------------------------

static bool wait_stopped(pid_t pid)
{
    int64_t deadline = now_ms() + 3000;
    while (now_ms() < deadline) {
        int status = 0;
        pid_t waited = waitpid(pid, &status, WUNTRACED | WNOHANG);
        if (waited == pid) return WIFSTOPPED(status);
        if (waited < 0 && errno != EINTR) return false;
        sleep_ms(5);
    }
    return false;
}

typedef struct {
    pid_t pid;
    int io_fd;          // stdout+stderr pipe (stdio) or PTY master (pty)
    bool stopped_before_user_code;
    OSStatus dynamic_status;
} launch_t;

static void kill_and_reap(pid_t pid)
{
    (void)kill(pid, SIGKILL);
    (void)waitpid(pid, NULL, 0);
}

// PTY contract: the calling helper is already a session leader without a
// controlling terminal. It acquires the PTY slave as its controlling terminal,
// spawns the CLI suspended in a new process group, makes that group the
// foreground group, verifies, and resumes. The helper keeps only the master.
static launch_t launch_tracked(tracker_t *tracker, bool pty, const char *path, char *const argv[],
                               char *const envp[], const char *cwd, const verifier_t *verifier)
{
    launch_t result = { -1, -1, false, errSecSuccess };
    int io_read = -1, child_side = -1;
    if (pty) {
        struct winsize size = { 120, 400, 0, 0 };
        if (openpty(&io_read, &child_side, NULL, NULL, &size) != 0) die("openpty");
        if (ioctl(child_side, TIOCSCTTY, 0) != 0) die("TIOCSCTTY");
    } else {
        int pipe_fds[2];
        if (pipe(pipe_fds) != 0) die("pipe");
        io_read = pipe_fds[0];
        child_side = pipe_fds[1];
    }
    (void)fcntl(io_read, F_SETFD, FD_CLOEXEC);
    (void)fcntl(child_side, F_SETFD, FD_CLOEXEC);

    posix_spawnattr_t attributes;
    posix_spawn_file_actions_t actions;
    posix_spawnattr_init(&attributes);
    posix_spawn_file_actions_init(&actions);
    sigset_t all_signals, no_signals;
    sigfillset(&all_signals);
    sigemptyset(&no_signals);
    posix_spawnattr_setsigdefault(&attributes, &all_signals);
    posix_spawnattr_setsigmask(&attributes, &no_signals);
    posix_spawnattr_setpgroup(&attributes, 0);
    posix_spawnattr_setflags(&attributes, POSIX_SPAWN_START_SUSPENDED | POSIX_SPAWN_SETPGROUP |
                             POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK);
    if (pty) {
        posix_spawn_file_actions_adddup2(&actions, child_side, 0);
    } else {
        posix_spawn_file_actions_addopen(&actions, 0, "/dev/null", O_RDONLY, 0);
    }
    posix_spawn_file_actions_adddup2(&actions, child_side, 1);
    posix_spawn_file_actions_adddup2(&actions, child_side, 2);
    if (cwd != NULL) posix_spawn_file_actions_addchdir(&actions, cwd);

    pid_t pid = -1;
    int spawn_status = posix_spawn(&pid, path, &actions, &attributes, argv, envp);
    posix_spawn_file_actions_destroy(&actions);
    posix_spawnattr_destroy(&attributes);
    close(child_side);
    if (spawn_status != 0) { errno = spawn_status; die("posix_spawn"); }
    if (!wait_stopped(pid)) { kill_and_reap(pid); fprintf(stderr, "child did not stop before user code\n"); exit(2); }
    result.stopped_before_user_code = true;
    tracker_set_root(tracker, pid);
    if (pty) {
        int ctty = open("/dev/tty", O_RDWR | O_CLOEXEC);
        if (ctty < 0 || tcsetpgrp(ctty, pid) != 0) { kill_and_reap(pid); die("tcsetpgrp"); }
        close(ctty);
    }
    if (verifier != NULL) {
        result.dynamic_status = verify_dynamic_pid(pid, verifier);
        if (result.dynamic_status != errSecSuccess) {
            kill_and_reap(pid);
            tracker->items[0].alive = false;
            close(io_read);
            return result;
        }
    }
    if (kill(pid, SIGCONT) != 0) { kill_and_reap(pid); die("SIGCONT"); }
    (void)fcntl(io_read, F_SETFL, O_NONBLOCK);
    result.pid = pid;
    result.io_fd = io_read;
    return result;
}

static size_t drain(int fd, char *buffer, size_t capacity, size_t used)
{
    char scratch[4096];
    for (;;) {
        ssize_t count = read(fd, scratch, sizeof(scratch));
        if (count <= 0) break;
        size_t copy = (size_t)count;
        if (buffer != NULL && used < capacity) {
            if (copy > capacity - used) copy = capacity - used;
            memcpy(buffer + used, scratch, copy);
            used += copy;
        }
    }
    return used;
}

// ---------------------------------------------------------------------------
// Fake CLIs
// ---------------------------------------------------------------------------

static void write_truth(const char *path, const char *text)
{
    char temporary[PATH_MAX];
    (void)snprintf(temporary, sizeof(temporary), "%s.tmp", path);
    int fd = open(temporary, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (fd < 0) _exit(120);
    if (write(fd, text, strlen(text)) < 0) _exit(121);
    close(fd);
    if (rename(temporary, path) != 0) _exit(122);
}

static void idle_forever(void)
{
    for (;;) pause();
}

static int fake_main(int argc, char **argv)
{
    if (argc < 4) return 64;
    const char *kind = argv[2];
    const char *truth = argv[3];
    signal(SIGTERM, SIG_IGN);
    signal(SIGHUP, SIG_IGN);
    alarm(30);
    char info[160];
    int foreground = isatty(0) ? (int)tcgetpgrp(0) : -1;
    int length = snprintf(info, sizeof(info), "INFO sid=%d pgid=%d tty=%d fg=%d\n", getsid(0), getpgrp(), isatty(0), foreground);
    if (write(1, info, (size_t)length) < 0) { }
    char text[128];
    if (strcmp(kind, "escape") == 0) {
        pid_t child = fork();
        if (child == 0) {
            alarm(30);
            if (setsid() < 0) _exit(130);
            (void)snprintf(text, sizeof(text), "%d\n", getpid());
            write_truth(truth, text);
            idle_forever();
        }
        idle_forever();
    }
    if (strcmp(kind, "double") == 0 || strcmp(kind, "double-scrub") == 0) {
        bool scrub = strcmp(kind, "double-scrub") == 0;
        pid_t child = fork();
        if (child == 0) {
            pid_t grandchild = fork();
            if (grandchild == 0) {
                alarm(30);
                if (setsid() < 0) _exit(131);
                (void)snprintf(text, sizeof(text), "%d\n", getpid());
                write_truth(truth, text);
                if (scrub) {
                    char *empty[] = { NULL };
                    execle("/bin/sleep", "sleep", "30", (char *)NULL, empty);
                    _exit(132);
                }
                idle_forever();
            }
            _exit(0);
        }
        (void)waitpid(child, NULL, 0);
        idle_forever();
    }
    if (strcmp(kind, "plain") == 0) {
        pid_t child = fork();
        if (child == 0) { alarm(30); idle_forever(); }
        (void)snprintf(text, sizeof(text), "%d %d\n", getpid(), child);
        write_truth(truth, text);
        idle_forever();
    }
    return 64;
}

// ---------------------------------------------------------------------------
// Self-test
// ---------------------------------------------------------------------------

static char scratch_dir[PATH_MAX];

static int read_truth_pids(const char *path, pid_t *pids, int capacity, int wait_ms)
{
    int64_t deadline = now_ms() + wait_ms;
    for (;;) {
        FILE *file = fopen(path, "r");
        if (file != NULL) {
            int count = 0, value;
            while (count < capacity && fscanf(file, "%d", &value) == 1) pids[count++] = (pid_t)value;
            fclose(file);
            if (count > 0) return count;
        }
        if (now_ms() >= deadline) return 0;
        sleep_ms(10);
    }
}

static bool pid_alive_nonzombie(pid_t pid) { return live(snapshot(pid)); }

static void external_cleanup(pid_t pid)
{
    if (pid_alive_nonzombie(pid)) (void)kill(pid, SIGKILL);
}

typedef struct {
    int tracked_by_tree;
    int tracked_by_orphan_scan;
    int escapes;
    int residual_tracked;
    char info[160];
    pid_t tracked[64];
    int tracked_count;
} trial_report_t;

// Runs in a helper that is its own session leader (product layout).
static void trial_helper(bool pty, const char *kind, const char *truth, int report_fd, int observe_ms)
{
    if (setsid() < 0) _exit(140);
    signal(SIGTTOU, SIG_IGN);
    signal(SIGHUP, SIG_IGN);
    static tracker_t tracker;
    tracker_init(&tracker, NULL);
    char *argv[] = { self_path, "--fake", (char *)kind, (char *)truth, NULL };
    char *envp[] = { tracker.token, NULL };
    launch_t launch = launch_tracked(&tracker, pty, self_path, argv, envp, NULL, NULL);
    char output[1024];
    size_t used = 0;
    int64_t deadline = now_ms() + observe_ms;
    while (now_ms() < deadline) {
        tracker_pump(&tracker, 10);
        used = drain(launch.io_fd, output, sizeof(output) - 1, used);
    }
    output[used] = '\0';
    trial_report_t report;
    memset(&report, 0, sizeof(report));
    char *line = strstr(output, "INFO ");
    if (line != NULL) {
        size_t length = strcspn(line, "\r\n");
        if (length >= sizeof(report.info)) length = sizeof(report.info) - 1;
        memcpy(report.info, line, length);
    }
    for (int index = 0; index < tracker.count && report.tracked_count < 64; index++) {
        report.tracked[report.tracked_count++] = tracker.items[index].pid;
        if (tracker.items[index].from_orphan_scan) report.tracked_by_orphan_scan++;
        else report.tracked_by_tree++;
    }
    report.escapes = tracker.escapes;
    report.residual_tracked = tracker_terminate(&tracker, 200, 2000, NULL);
    if (write(report_fd, &report, sizeof(report)) != (ssize_t)sizeof(report)) _exit(141);
    _exit(0);
}

typedef struct { int runs; int escape_detected; int truth_tracked; int by_orphan_scan; int residual; int info_ok; } trial_summary_t;

static bool info_matches(const char *info, bool pty)
{
    int sid = 0, pgid = 0, tty = 0, foreground = 0;
    if (sscanf(info, "INFO sid=%d pgid=%d tty=%d fg=%d", &sid, &pgid, &tty, &foreground) != 4) return false;
    if (pty) return tty == 1 && foreground == pgid && sid != pgid;
    return tty == 0 && sid != pgid;
}

static trial_summary_t run_trials(bool pty, const char *kind, int runs, int observe_ms)
{
    trial_summary_t summary = { 0, 0, 0, 0, 0, 0 };
    for (int run = 0; run < runs; run++) {
        char truth[PATH_MAX];
        (void)snprintf(truth, sizeof(truth), "%s/truth-%s-%d-%d", scratch_dir, kind, pty, run);
        unlink(truth);
        int report_pipe[2];
        if (pipe(report_pipe) != 0) die("report pipe");
        pid_t helper = fork();
        if (helper < 0) die("fork helper");
        if (helper == 0) {
            close(report_pipe[0]);
            trial_helper(pty, kind, truth, report_pipe[1], observe_ms);
        }
        close(report_pipe[1]);
        trial_report_t report;
        memset(&report, 0, sizeof(report));
        ssize_t got = read(report_pipe[0], &report, sizeof(report));
        close(report_pipe[0]);
        int status = 0;
        (void)waitpid(helper, &status, 0);
        pid_t truth_pids[4];
        int truth_count = read_truth_pids(truth, truth_pids, 4, 2000);
        summary.runs++;
        if (got != (ssize_t)sizeof(report) || truth_count == 0) {
            fprintf(stderr, "trial %s/%s#%d incomplete got=%zd truth=%d status=%d\n", kind, pty ? "pty" : "stdio", run, got, truth_count, status);
            summary.residual++;
            for (int index = 0; index < truth_count; index++) external_cleanup(truth_pids[index]);
            continue;
        }
        pid_t target = truth_pids[0];
        bool tracked = false;
        for (int index = 0; index < report.tracked_count; index++) if (report.tracked[index] == target) tracked = true;
        if (tracked) summary.truth_tracked++;
        if (report.escapes > 0) summary.escape_detected++;
        summary.by_orphan_scan += report.tracked_by_orphan_scan;
        if (info_matches(report.info, pty)) summary.info_ok++;
        sleep_ms(50);
        if (pid_alive_nonzombie(target) || report.residual_tracked > 0) {
            summary.residual++;
            external_cleanup(target);
        }
    }
    printf("TRIALS mode=%s kind=%s runs=%d escape_detected=%d truth_tracked=%d found_by_orphan_scan=%d residual_after_helper_cleanup=%d contract_info_ok=%d\n",
           pty ? "pty" : "stdio", kind, summary.runs, summary.escape_detected, summary.truth_tracked,
           summary.by_orphan_scan, summary.residual, summary.info_ok);
    return summary;
}

// App/helper layout test: helper in its own session vs. legacy shared group.
typedef enum { ACTION_APP_GROUP_KILL, ACTION_BOTH_KILL } owner_action_t;

typedef struct { pid_t helper; pid_t root; int residual; bool done; } owner_status_t;

static void owner_app(bool separate_helper, bool pty, const char *journal, const char *truth, int status_fd)
{
    if (setpgid(0, 0) != 0) _exit(150);
    alarm(30);
    int life[2];
    if (pipe(life) != 0) _exit(151);
    pid_t helper = fork();
    if (helper < 0) _exit(152);
    if (helper == 0) {
        close(life[1]);
        if (separate_helper && setsid() < 0) _exit(153);
        signal(SIGTTOU, SIG_IGN);
        signal(SIGHUP, SIG_IGN);
        static tracker_t tracker;
        tracker_init(&tracker, journal);
        char *argv[] = { self_path, "--fake", "plain", (char *)truth, NULL };
        char *envp[] = { tracker.token, NULL };
        launch_t launch = launch_tracked(&tracker, pty, self_path, argv, envp, NULL, NULL);
        pid_t truth_pids[2];
        (void)read_truth_pids(truth, truth_pids, 2, 2000);
        tracker_pump(&tracker, 20);
        dprintf(status_fd, "READY helper=%d root=%d\n", getpid(), launch.pid);
        for (;;) {
            tracker_pump(&tracker, 20);
            (void)drain(launch.io_fd, NULL, 0, 0);
            struct pollfd poll_fd = { life[0], POLLIN, 0 };
            if (poll(&poll_fd, 1, 0) > 0) {
                char byte;
                if (read(life[0], &byte, 1) <= 0) break;
            }
        }
        int residual = tracker_terminate(&tracker, 200, 2000, NULL);
        dprintf(status_fd, "DONE residual=%d\n", residual);
        _exit(0);
    }
    close(life[0]);
    idle_forever();
}

static bool read_line_timeout(int fd, char *buffer, size_t capacity, int timeout_ms)
{
    size_t used = 0;
    int64_t deadline = now_ms() + timeout_ms;
    while (used + 1 < capacity) {
        int remaining = (int)(deadline - now_ms());
        if (remaining <= 0) return false;
        struct pollfd poll_fd = { fd, POLLIN, 0 };
        if (poll(&poll_fd, 1, remaining) <= 0) return false;
        ssize_t count = read(fd, buffer + used, 1);
        if (count <= 0) return false;
        if (buffer[used] == '\n') { buffer[used] = '\0'; return true; }
        used++;
    }
    return false;
}

typedef struct { int runs; int helper_cleaned; int residual_before_sweep; int swept_clean; int sweep_killed; } owner_summary_t;

static owner_summary_t run_owner_trials(bool separate_helper, bool pty, owner_action_t action, int runs)
{
    owner_summary_t summary = { 0, 0, 0, 0, 0 };
    for (int run = 0; run < runs; run++) {
        char journal[PATH_MAX], truth[PATH_MAX];
        (void)snprintf(journal, sizeof(journal), "%s/journal-%d-%d-%d-%d", scratch_dir, separate_helper, pty, action, run);
        (void)snprintf(truth, sizeof(truth), "%s/owner-truth-%d-%d-%d-%d", scratch_dir, separate_helper, pty, action, run);
        unlink(journal);
        unlink(truth);
        int status_pipe[2];
        if (pipe(status_pipe) != 0) die("status pipe");
        pid_t app = fork();
        if (app < 0) die("fork app");
        if (app == 0) {
            close(status_pipe[0]);
            owner_app(separate_helper, pty, journal, truth, status_pipe[1]);
        }
        close(status_pipe[1]);
        char line[256];
        pid_t helper = -1, root = -1;
        summary.runs++;
        if (!read_line_timeout(status_pipe[0], line, sizeof(line), 5000) ||
            sscanf(line, "READY helper=%d root=%d", &helper, &root) != 2) {
            fprintf(stderr, "owner trial not ready: %s\n", line);
            kill(-app, SIGKILL);
            (void)waitpid(app, NULL, 0);
            close(status_pipe[0]);
            summary.residual_before_sweep++;
            continue;
        }
        pid_t truth_pids[2];
        int truth_count = read_truth_pids(truth, truth_pids, 2, 2000);
        if (action == ACTION_APP_GROUP_KILL) {
            (void)kill(-app, SIGKILL);
        } else {
            (void)kill(helper, SIGKILL);
            (void)kill(app, SIGKILL);
        }
        (void)waitpid(app, NULL, 0);
        bool helper_reported = read_line_timeout(status_pipe[0], line, sizeof(line), 4000);
        int helper_residual = -1;
        if (helper_reported && sscanf(line, "DONE residual=%d", &helper_residual) == 1 && helper_residual == 0)
            summary.helper_cleaned++;
        close(status_pipe[0]);
        sleep_ms(100);
        int residual = 0;
        for (int index = 0; index < truth_count; index++) if (pid_alive_nonzombie(truth_pids[index])) residual++;
        if (residual > 0) {
            summary.residual_before_sweep++;
            sweep_t sweep = sweep_journal(journal);
            summary.sweep_killed += sweep.killed;
            int after = 0;
            for (int index = 0; index < truth_count; index++) if (pid_alive_nonzombie(truth_pids[index])) after++;
            if (after == 0 && sweep.residual == 0) summary.swept_clean++;
            for (int index = 0; index < truth_count; index++) external_cleanup(truth_pids[index]);
        }
        external_cleanup(helper);
    }
    printf("OWNERS layout=%s mode=%s action=%s runs=%d helper_cleaned=%d residual_before_sweep=%d next_launch_sweep_clean=%d sweep_killed=%d\n",
           separate_helper ? "helper-own-session" : "helper-in-app-group", pty ? "pty" : "stdio",
           action == ACTION_APP_GROUP_KILL ? "app-group-SIGKILL" : "app+helper-SIGKILL",
           summary.runs, summary.helper_cleaned, summary.residual_before_sweep, summary.swept_clean, summary.sweep_killed);
    return summary;
}

static bool sweep_negative_controls(void)
{
    pid_t sentinel = fork();
    if (sentinel == 0) { alarm(10); idle_forever(); }
    sleep_ms(50);
    snapshot_t value = snapshot(sentinel);
    pid_t exited = fork();
    if (exited == 0) _exit(0);
    snapshot_t exited_value = snapshot(exited);
    (void)waitpid(exited, NULL, 0);
    char journal[PATH_MAX];
    (void)snprintf(journal, sizeof(journal), "%s/negative-journal", scratch_dir);
    FILE *file = fopen(journal, "w");
    if (file == NULL) die("negative journal");
    fprintf(file, "%d %llu\n", sentinel, (unsigned long long)(value.start_us + 1));
    fprintf(file, "%d %llu\n", sentinel, (unsigned long long)(value.start_us - 1000000));
    fprintf(file, "%d %llu\n", exited, (unsigned long long)exited_value.start_us);
    fclose(file);
    sweep_t sweep = sweep_journal(journal);
    bool sentinel_alive = pid_alive_nonzombie(sentinel);
    kill_and_reap(sentinel);
    bool ok = sweep.killed == 0 && sweep.skipped_identity == 2 && sentinel_alive;
    printf("SWEEP_NEGATIVE start_time_mismatch_skipped=%d exited_entries=%d killed=%d sentinel_survived=%d\n",
           sweep.skipped_identity, sweep.gone, sweep.killed, sentinel_alive);
    return ok;
}

static int selftest_main(void)
{
    char template_path[] = "/private/tmp/ai-usage-tracker-probe.XXXXXXXX";
    if (mkdtemp(template_path) == NULL) die("mkdtemp");
    (void)snprintf(scratch_dir, sizeof(scratch_dir), "%s", template_path);
    bool ok = true;

    trial_summary_t escape_stdio = run_trials(false, "escape", 20, 150);
    trial_summary_t escape_pty = run_trials(true, "escape", 20, 150);
    ok &= escape_stdio.escape_detected == 20 && escape_stdio.truth_tracked == 20 && escape_stdio.residual == 0 && escape_stdio.info_ok == 20;
    ok &= escape_pty.escape_detected == 20 && escape_pty.truth_tracked == 20 && escape_pty.residual == 0 && escape_pty.info_ok == 20;

    trial_summary_t orphan = run_trials(false, "double", 30, 200);
    ok &= orphan.truth_tracked == 30 && orphan.residual == 0;
    // Known limitation, measured but not a pass condition: an orphan that execs
    // with a scrubbed environment carries no session token.
    trial_summary_t scrubbed = run_trials(false, "double-scrub", 30, 200);
    printf("LIMITATION double-fork+env-scrub residual=%d/%d\n", scrubbed.residual, scrubbed.runs);

    owner_summary_t own_session = run_owner_trials(true, false, ACTION_APP_GROUP_KILL, 10);
    owner_summary_t own_session_pty = run_owner_trials(true, true, ACTION_APP_GROUP_KILL, 5);
    owner_summary_t legacy = run_owner_trials(false, false, ACTION_APP_GROUP_KILL, 3);
    owner_summary_t both_stdio = run_owner_trials(true, false, ACTION_BOTH_KILL, 10);
    owner_summary_t both_pty = run_owner_trials(true, true, ACTION_BOTH_KILL, 5);
    ok &= own_session.helper_cleaned == 10 && own_session.residual_before_sweep == 0;
    ok &= own_session_pty.helper_cleaned == 5 && own_session_pty.residual_before_sweep == 0;
    ok &= legacy.residual_before_sweep == 3 && legacy.swept_clean == 3;
    ok &= both_stdio.residual_before_sweep == 10 && both_stdio.swept_clean == 10;
    ok &= both_pty.residual_before_sweep == both_pty.swept_clean;
    ok &= sweep_negative_controls();

    printf("%s scratch=%s\n", ok ? "PASS" : "FAIL", scratch_dir);
    return ok ? 0 : 1;
}

// ---------------------------------------------------------------------------
// Observe mode (real CLI, signature-gated)
// ---------------------------------------------------------------------------

static const char *const trust_anchors[] = { "trust this folder", "Is this a project you created or one you trust", NULL };
static const char *const signed_out_anchors[] = { "Sign in to Claude", "Please run /login", "Invalid API key", "Not logged in", NULL };
static const char *const setup_anchors[] = { "Choose the text style", "Select login method", "Let's get started", "Press Enter to continue", NULL };
// The screen-reader banner is printed before the trust dialog too, so a ready
// anchor alone never authorizes input. Like the product parser, Ready also
// requires a prompt line (a line that is "$", or a box line starting with "> ").
static const char *const ready_anchors[] = { "? for shortcuts", "Try \"", "[Screen Reader Mode: on via flag]", NULL };
static const char *const any_screen_anchors[] = { "[Screen Reader Mode: on via flag]", NULL };
static const char *const usage_anchors[] = { "Current session", "Current week (all models)", NULL };

static bool contains_any(const char *text, const char *const anchors[])
{
    for (int index = 0; anchors[index] != NULL; index++)
        if (strcasestr(text, anchors[index]) != NULL) return true;
    return false;
}

static bool has_prompt_line(const char *text)
{
    const char *line = text;
    while (*line != '\0') {
        const char *end = strchr(line, '\n');
        size_t length = end != NULL ? (size_t)(end - line) : strlen(line);
        size_t start = 0;
        while (start < length && line[start] == ' ') start++;
        size_t stop = length;
        while (stop > start && line[stop - 1] == ' ') stop--;
        if (stop - start == 1 && line[start] == '$') return true;
        if (stop - start >= 2 && (line[start] == '|' || strncmp(line + start, "\xe2\x94\x82", 3) == 0)) {
            size_t next = start + (line[start] == '|' ? 1 : 3);
            while (next < stop && line[next] == ' ') next++;
            if (next < stop && line[next] == '>') return true;
        }
        if (end == NULL) break;
        line = end + 1;
    }
    return false;
}

typedef struct {
    char *text;
    size_t length;
    size_t capacity;
    int state;  // 0 text, 1 ESC, 2 CSI, 3 OSC, 4 OSC-ESC
    int dsr_queries;
    int da_queries;
    char csi[32];
    size_t csi_length;
} screen_t;

// Strips escape sequences so anchors can be matched; answers cursor-position and
// primary device-attribute queries so a TUI waiting on them can progress.
static void screen_feed(screen_t *screen, int master, const char *data, size_t length)
{
    for (size_t index = 0; index < length; index++) {
        unsigned char byte = (unsigned char)data[index];
        switch (screen->state) {
        case 0:
            if (byte == 0x1b) { screen->state = 1; break; }
            if ((byte >= 0x20 || byte == '\n') && screen->length + 1 < screen->capacity)
                screen->text[screen->length++] = (char)(byte == '\r' ? '\n' : byte);
            else if (byte == '\r' && screen->length + 1 < screen->capacity)
                screen->text[screen->length++] = '\n';
            break;
        case 1:
            if (byte == '[') { screen->state = 2; screen->csi_length = 0; }
            else if (byte == ']') screen->state = 3;
            else screen->state = 0;
            break;
        case 2:
            if (byte >= 0x40 && byte <= 0x7e) {
                screen->csi[screen->csi_length] = '\0';
                if (byte == 'n' && strcmp(screen->csi, "6") == 0) {
                    screen->dsr_queries++;
                    if (write(master, "\x1b[1;1R", 6) < 0) { }
                } else if (byte == 'c' && (screen->csi_length == 0 || strcmp(screen->csi, "0") == 0)) {
                    screen->da_queries++;
                    if (write(master, "\x1b[?62;22c", 9) < 0) { }
                } else if (screen->length + 1 < screen->capacity && (byte == 'C' || byte == 'G' || byte == 'H')) {
                    screen->text[screen->length++] = ' ';
                }
                screen->state = 0;
            } else if (screen->csi_length + 1 < sizeof(screen->csi)) {
                screen->csi[screen->csi_length++] = (char)byte;
            }
            break;
        case 3:
            if (byte == 0x07) screen->state = 0;
            else if (byte == 0x1b) screen->state = 4;
            break;
        default:
            screen->state = 0;
            break;
        }
    }
    screen->text[screen->length] = '\0';
}

static size_t pump_pty(tracker_t *tracker, int master, screen_t *screen, int milliseconds)
{
    size_t total = 0;
    int64_t deadline = now_ms() + milliseconds;
    do {
        struct pollfd poll_fd = { master, POLLIN, 0 };
        (void)poll(&poll_fd, 1, 10);
        char buffer[8192];
        for (;;) {
            ssize_t count = read(master, buffer, sizeof(buffer));
            if (count <= 0) break;
            total += (size_t)count;
            screen_feed(screen, master, buffer, (size_t)count);
        }
        tracker_pump(tracker, 0);
    } while (now_ms() < deadline);
    return total;
}

static void print_process_report(tracker_t *tracker)
{
    char names[512] = "";
    for (int index = 0; index < tracker->count; index++) {
        if (strstr(names, tracker->items[index].comm) != NULL) continue;
        if (strlen(names) + strlen(tracker->items[index].comm) + 2 >= sizeof(names)) break;
        if (names[0] != '\0') strcat(names, ",");
        strcat(names, tracker->items[index].comm);
    }
    printf("PROC tracked_total=%d max_alive=%d escapes=%d found_by_orphan_scan=%d overflow=%d names=%s\n",
           tracker->count, tracker->max_alive, tracker->escapes, tracker->orphan_hits, tracker->overflow, names);
    for (int index = 0; index < tracker->count; index++) {
        tracked_t *item = &tracker->items[index];
        if (!item->escaped) continue;
        printf("ESCAPE comm=%s pgid_changed=%d sid_changed=%d via_orphan_scan=%d\n", item->comm,
               item->pgid != tracker->root_pgid, item->sid != tracker->root_sid, item->from_orphan_scan);
    }
}

static int observe_helper(const char *mode, const char *team_id, const char *binary, const char *cwd, int argc, char **argv)
{
    bool pty = strncmp(mode, "pty", 3) == 0;
    bool hangup_test = strcmp(mode, "pty-hup") == 0;
    if (setsid() < 0) die("setsid");
    signal(SIGTTOU, SIG_IGN);
    signal(SIGPIPE, SIG_IGN);
    // The helper becomes the controlling process of the PTY session; closing the
    // master delivers SIGHUP to it as well as to the foreground group.
    signal(SIGHUP, SIG_IGN);

    char requirement_text[512];
    (void)snprintf(requirement_text, sizeof(requirement_text),
        "identifier \"com.anthropic.claude-code\" and anchor apple generic and "
        "certificate 1[field.1.2.840.113635.100.6.2.6] exists and "
        "certificate leaf[field.1.2.840.113635.100.6.1.13] exists and "
        "certificate leaf[subject.OU] = \"%s\"", team_id);
    CFStringRef requirement_string = CFStringCreateWithCString(NULL, requirement_text, kCFStringEncodingUTF8);
    verifier_t verifier = { NULL, NULL };
    if (SecRequirementCreateWithString(requirement_string, kSecCSDefaultFlags, &verifier.requirement) != errSecSuccess) die("requirement");
    CFRelease(requirement_string);

    int fd = open(binary, O_RDONLY | O_CLOEXEC);
    if (fd < 0) die("open binary");
    struct stat before_fd, before_path, after_fd, after_path;
    if (fstat(fd, &before_fd) != 0 || stat(binary, &before_path) != 0) die("stat binary");
    OSStatus static_status = verify_static_fd(fd, &verifier);
    if (fstat(fd, &after_fd) != 0 || stat(binary, &after_path) != 0) die("stat binary");
    bool identity_stable = before_fd.st_dev == before_path.st_dev && before_fd.st_ino == before_path.st_ino &&
        after_fd.st_ino == before_fd.st_ino && after_path.st_ino == before_fd.st_ino &&
        after_fd.st_size == before_fd.st_size && after_fd.st_mtimespec.tv_sec == before_fd.st_mtimespec.tv_sec;
    printf("VERIFY static=%d identity_stable=%d size=%lld\n", (int)static_status, identity_stable, (long long)before_fd.st_size);
    if (static_status != errSecSuccess || !identity_stable) { printf("RESULT not-launched reason=static-verification\n"); return 3; }

    static tracker_t tracker;
    tracker_init(&tracker, NULL);
    struct passwd *account = getpwuid(getuid());
    if (account == NULL) die("getpwuid");
    static char home[PATH_MAX + 8], user[256], logname[256], tmpdir[PATH_MAX + 8];
    (void)snprintf(home, sizeof(home), "HOME=%s", account->pw_dir);
    (void)snprintf(user, sizeof(user), "USER=%s", account->pw_name);
    (void)snprintf(logname, sizeof(logname), "LOGNAME=%s", account->pw_name);
    const char *temporary = getenv("TMPDIR");
    (void)snprintf(tmpdir, sizeof(tmpdir), "TMPDIR=%s", temporary != NULL ? temporary : "/private/tmp/");
    char *envp[] = { home, user, logname, tmpdir, "PATH=/usr/bin:/bin:/usr/sbin:/sbin", "LANG=en_US.UTF-8",
                     pty ? "TERM=xterm-256color" : "TERM=dumb", tracker.token, NULL };
    char *child_argv[64];
    int child_argc = 0;
    child_argv[child_argc++] = (char *)binary;
    for (int index = 0; index < argc && child_argc < 63; index++) child_argv[child_argc++] = argv[index];
    child_argv[child_argc] = NULL;

    launch_t launch = launch_tracked(&tracker, pty, binary, child_argv, envp, cwd, &verifier);
    printf("LAUNCH stopped_before_user_code=%d dynamic_signature_and_unique=%d\n", launch.stopped_before_user_code, (int)launch.dynamic_status);
    if (launch.pid < 0) { printf("RESULT not-launched reason=dynamic-verification\n"); return 3; }

    if (!pty) {
        static char output[256 * 1024];
        size_t used = 0;
        int64_t deadline = now_ms() + 20000;
        while (!tracker.root_reaped && now_ms() < deadline) {
            tracker_pump(&tracker, 20);
            used = drain(launch.io_fd, output, sizeof(output) - 1, used);
        }
        used = drain(launch.io_fd, output, sizeof(output) - 1, used);
        output[used] = '\0';
        tracker_refresh(&tracker);
        int alive_after_root = tracker_alive(&tracker);
        bool is_version = false, is_help = false;
        for (int index = 0; index < argc; index++) {
            if (strcmp(argv[index], "--version") == 0) is_version = true;
            if (strcmp(argv[index], "--help") == 0) is_help = true;
        }
        printf("STDIO root_exited=%d exit_code=%d output_bytes=%zu alive_after_root_exit=%d\n", tracker.root_reaped,
               tracker.root_reaped && WIFEXITED(tracker.root_status) ? WEXITSTATUS(tracker.root_status) : -1, used, alive_after_root);
        if (is_version) {
            size_t length = strcspn(output, "\r\n");
            if (length > 80) length = 80;
            printf("VERSION %.*s\n", (int)length, output);
        }
        if (is_help) {
            const char *flags[] = { "--setting-sources", "--settings", "--tools", "--no-chrome", "--strict-mcp-config", "--safe-mode", "--ax-screen-reader", NULL };
            printf("HELP_FLAGS");
            for (int index = 0; flags[index] != NULL; index++) printf(" %s=%d", flags[index], strstr(output, flags[index]) != NULL);
            printf("\n");
        }
        memset(output, 0, used);
    } else {
        static char text[1024 * 1024];
        screen_t screen;
        memset(&screen, 0, sizeof(screen));
        screen.text = text;
        screen.capacity = sizeof(text);
        size_t bytes = 0;
        bool ready = false, trust = false, signed_out = false, setup = false, usage = false;
        // Classify only after the output has been quiet for 3 s. In screen-reader
        // mode the "[Screen Reader Mode: on via flag]" banner precedes the trust
        // dialog, so an early anchor match must never authorize input.
        int64_t deadline = now_ms() + 30000;
        int64_t last_output = now_ms();
        while (now_ms() < deadline && !tracker.root_reaped) {
            size_t received = pump_pty(&tracker, launch.io_fd, &screen, 100);
            bytes += received;
            if (received > 0) last_output = now_ms();
            bool any_anchor = contains_any(screen.text, trust_anchors) || contains_any(screen.text, signed_out_anchors) ||
                contains_any(screen.text, setup_anchors) || contains_any(screen.text, ready_anchors) ||
                contains_any(screen.text, any_screen_anchors);
            if (any_anchor && now_ms() - last_output >= 3000) break;
        }
        trust = contains_any(screen.text, trust_anchors);
        signed_out = contains_any(screen.text, signed_out_anchors);
        setup = contains_any(screen.text, setup_anchors);
        ready = !trust && !signed_out && !setup && contains_any(screen.text, ready_anchors) && has_prompt_line(screen.text);
        int64_t ready_ms = now_ms();
        if (ready && !trust && !signed_out && !setup) {
            bytes += pump_pty(&tracker, launch.io_fd, &screen, 1500);
            size_t mark = screen.length;
            if (write(launch.io_fd, "/usage", 6) < 0) { }
            bytes += pump_pty(&tracker, launch.io_fd, &screen, 400);
            if (write(launch.io_fd, "\r", 1) < 0) { }
            int64_t usage_deadline = now_ms() + 15000;
            while (now_ms() < usage_deadline && !tracker.root_reaped) {
                bytes += pump_pty(&tracker, launch.io_fd, &screen, 100);
                if (contains_any(screen.text + mark, usage_anchors)) { usage = true; break; }
            }
            bytes += pump_pty(&tracker, launch.io_fd, &screen, 1000);
            if (write(launch.io_fd, "\x1b", 1) < 0) { }
            bytes += pump_pty(&tracker, launch.io_fd, &screen, 1500);
        }
        (void)ready_ms;
        int exit_code = tracker.root_reaped && WIFEXITED(tracker.root_status) ? WEXITSTATUS(tracker.root_status) : -1;
        int exit_signal = tracker.root_reaped && WIFSIGNALED(tracker.root_status) ? WTERMSIG(tracker.root_status) : 0;
        printf("SCREEN ready=%d trust_prompt=%d signed_out=%d setup=%d usage_screen=%d bytes=%zu dsr_queries=%d da_queries=%d root_exited_early=%d exit_code=%d exit_signal=%d\n",
               ready, trust, signed_out, setup, usage, bytes, screen.dsr_queries, screen.da_queries, tracker.root_reaped,
               exit_code, exit_signal);
        // Opt-in diagnostics for screens that are NOT the usage screen: digits and
        // e-mail-like words are masked, output is truncated, usage screens are never printed.
        const char *diagnostics = getenv("AIUSAGE_OBSERVE_DIAG");
        if (diagnostics != NULL && strcmp(diagnostics, "1") == 0 && !usage) {
            size_t limit = screen.length < 1500 ? screen.length : 1500;
            printf("DIAG_BEGIN\n");
            size_t word_start = 0;
            for (size_t index = 0; index <= limit; index++) {
                if (index < limit && screen.text[index] != ' ' && screen.text[index] != '\n') continue;
                bool email = memchr(screen.text + word_start, '@', index - word_start) != NULL;
                for (size_t position = word_start; position < index; position++) {
                    char character = screen.text[position];
                    putchar(email ? '*' : (character >= '0' && character <= '9') ? '#' : character);
                }
                if (index < limit) putchar(screen.text[index]);
                word_start = index + 1;
            }
            printf("\nDIAG_END\n");
        }
        memset(text, 0, screen.length);
        if (hangup_test) {
            close(launch.io_fd);
            int64_t hangup_start = now_ms();
            const char *wait_text = getenv("AIUSAGE_HUP_WAIT_MS");
            int hangup_wait_ms = wait_text != NULL ? atoi(wait_text) : 5000;
            if (hangup_wait_ms < 1000 || hangup_wait_ms > 120000) hangup_wait_ms = 5000;
            while (tracker_alive(&tracker) > 0 && now_ms() - hangup_start < hangup_wait_ms) tracker_pump(&tracker, 20);
            printf("HANGUP master_closed=1 all_exited_within_wait=%d elapsed_ms=%lld root_alive=%d alive=", tracker_alive(&tracker) == 0,
                   (long long)(now_ms() - hangup_start), tracker.items[0].alive);
            for (int index = 0; index < tracker.count; index++)
                if (tracker.items[index].alive) printf("%s%s", tracker.items[index].comm, index + 1 < tracker.count ? "," : "");
            printf("\n");
        }
    }
    print_process_report(&tracker);
    tracker_refresh(&tracker);
    printf("BEFORE_TERM alive=");
    for (int index = 0; index < tracker.count; index++)
        if (tracker.items[index].alive) printf("%s,", tracker.items[index].comm);
    printf("\n");
    bool term_sufficient = false;
    int residual = tracker_terminate(&tracker, 3000, 3000, &term_sufficient);
    printf("AFTER_TERM_GRACE term_sufficient=%d\n", term_sufficient);
    printf("TERMINATE term_within_grace=%d residual_tracked=%d\n", term_sufficient, residual);
    printf("RESULT %s\n", residual == 0 && tracker.escapes == 0 ? "contained" : (tracker.escapes > 0 ? "escape-detected" : "residual"));
    fflush(stdout);
    return residual == 0 && tracker.escapes == 0 ? 0 : 4;
}

static int observe_main(int argc, char **argv)
{
    if (argc < 6) {
        fprintf(stderr, "usage: %s --observe stdio|pty|pty-hup <team-id> <absolute-binary> <cwd> [args...]\n", argv[0]);
        return 64;
    }
    if (argv[4][0] != '/' || argv[5][0] != '/') { fprintf(stderr, "binary and cwd must be absolute\n"); return 64; }
    fflush(stdout);
    pid_t helper = fork();
    if (helper < 0) die("fork observe helper");
    if (helper == 0) {
        int code = observe_helper(argv[2], argv[3], argv[4], argv[5], argc - 6, argv + 6);
        fflush(stdout);
        _exit(code);
    }
    int status = 0;
    (void)waitpid(helper, &status, 0);
    return WIFEXITED(status) ? WEXITSTATUS(status) : 5;
}

#ifndef DESCENDANT_TRACKER_NO_MAIN
int main(int argc, char **argv)
{
    uint32_t size = sizeof(self_path);
    if (_NSGetExecutablePath(self_path, &size) != 0) die("executable path");
    char resolved[PATH_MAX];
    if (realpath(self_path, resolved) != NULL) (void)snprintf(self_path, sizeof(self_path), "%s", resolved);
    setvbuf(stdout, NULL, _IOLBF, 0);
    if (argc >= 2 && strcmp(argv[1], "--fake") == 0) return fake_main(argc, argv);
    if (argc >= 2 && strcmp(argv[1], "--observe") == 0) return observe_main(argc, argv);
    if (argc == 1 || (argc == 2 && strcmp(argv[1], "--selftest") == 0)) return selftest_main();
    fprintf(stderr, "usage: %s [--selftest] | --observe ...\n", argv[0]);
    return 64;
}
#endif
