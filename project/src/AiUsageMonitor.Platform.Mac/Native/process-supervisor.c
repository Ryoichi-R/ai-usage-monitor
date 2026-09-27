// Production stdio supervisor. Tracker derived from the P0-9 probe.
// No CLI screen, argument, environment, or usage payload is logged.
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
#include <sys/file.h>
#include <sys/socket.h>
#include <sys/un.h>

#define MAX_TRACKED 1024
#define MAX_NEGATIVE_CACHE 256
#define TOKEN_NAME "AIUSAGE_TRACK_TOKEN="



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
    return live(value) && value.uid == getuid() && value.start_us == start_us;
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
    int fd = open(temporary, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (fd < 0) { tracker->overflow++; return; }
    for (int index = 0; index < tracker->count; index++) {
        char line[96];
        int length = snprintf(line, sizeof(line), "%d %llu\n", tracker->items[index].pid,
                              (unsigned long long)tracker->items[index].start_us);
        if (write(fd, line, (size_t)length) != length) { tracker->overflow++; break; }
    }
    close(fd);
    if (rename(temporary, tracker->journal_path) != 0) tracker->overflow++;
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
    if (kevent(tracker->kq, &change, 1, NULL, 0, NULL) != 0) {
        if (errno == ESRCH) item->alive = false;
        else tracker->overflow++;
    }
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
    if (bytes <= 0 || bytes >= (int)sizeof(all)) { tracker->overflow++; return; }
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
            if (bytes >= (int)sizeof(children)) tracker->overflow++;
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
    int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) { if (errno != ENOENT) result.residual++; return result; }
    struct stat st;
    if (fstat(fd, &st) != 0 || !S_ISREG(st.st_mode) || st.st_uid != getuid() || (st.st_mode & 077) != 0) {
        close(fd); result.residual++; return result;
    }
    FILE *file = fdopen(fd, "r");
    if (file == NULL) { close(fd); result.residual++; return result; }
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



// Static validation is bound to an open file and compared with the suspended process.
#ifndef AIUSAGE_TEST_SIGNING
#define CLAUDE_REQUIREMENT "identifier \"com.anthropic.claude-code\" and anchor apple generic and certificate 1[field.1.2.840.113635.100.6.2.6] exists and certificate leaf[field.1.2.840.113635.100.6.1.13] exists and certificate leaf[subject.OU] = \"Q6L2SF6YDW\""
#else
#define CLAUDE_REQUIREMENT "identifier \"org.example.ai-usage-suspended-probe\""
#endif
static CFDataRef code_identity(SecStaticCodeRef code)
{
    CFDictionaryRef info = NULL;
    if (SecCodeCopySigningInformation(code, kSecCSSigningInformation, &info) != errSecSuccess) return NULL;
    CFTypeRef value = CFDictionaryGetValue(info, kSecCodeInfoUnique);
    CFDataRef result = value != NULL && CFGetTypeID(value) == CFDataGetTypeID() ? CFRetain(value) : NULL;
    CFRelease(info);
    return result;
}
static CFDataRef verify_claude_file(const char *path, int *opened, SecRequirementRef *requirement)
{
    *opened = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    struct stat before, after, current;
    if (*opened < 0 || fstat(*opened, &before) != 0 || !S_ISREG(before.st_mode)) return NULL;
    CFStringRef text = CFStringCreateWithCString(NULL, CLAUDE_REQUIREMENT, kCFStringEncodingUTF8);
    OSStatus status = SecRequirementCreateWithString(text, kSecCSDefaultFlags, requirement);
    CFRelease(text);
    if (status != errSecSuccess) return NULL;
    char fdpath[64]; snprintf(fdpath, sizeof(fdpath), "/dev/fd/%d", *opened);
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(NULL, (const UInt8 *)fdpath, strlen(fdpath), false);
    SecStaticCodeRef code = NULL;
    status = SecStaticCodeCreateWithPath(url, kSecCSDefaultFlags, &code);
    CFRelease(url);
    if (status != errSecSuccess) return NULL;
    CFDataRef identity = NULL;
    if (SecStaticCodeCheckValidity(code, kSecCSStrictValidate, *requirement) == errSecSuccess)
        identity = code_identity(code);
    CFRelease(code);
    if (fstat(*opened, &after) != 0 || stat(path, &current) != 0 ||
        before.st_dev != current.st_dev || before.st_ino != current.st_ino ||
        before.st_size != after.st_size || before.st_mtimespec.tv_sec != after.st_mtimespec.tv_sec ||
        before.st_mtimespec.tv_nsec != after.st_mtimespec.tv_nsec ||
        before.st_ctimespec.tv_sec != after.st_ctimespec.tv_sec || before.st_ctimespec.tv_nsec != after.st_ctimespec.tv_nsec) {
        if (identity != NULL) CFRelease(identity);
        return NULL;
    }
    return identity;
}
static bool verify_claude_process(pid_t pid, SecRequirementRef requirement, CFDataRef expected)
{
    CFNumberRef number = CFNumberCreate(NULL, kCFNumberIntType, &pid);
    const void *keys[] = { kSecGuestAttributePid }; const void *values[] = { number };
    CFDictionaryRef attributes = CFDictionaryCreate(NULL, keys, values, 1, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    SecCodeRef guest = NULL;
    OSStatus status = SecCodeCopyGuestWithAttributes(NULL, attributes, kSecCSDefaultFlags, &guest);
    CFRelease(attributes); CFRelease(number);
    if (status != errSecSuccess) return false;
    status = SecCodeCheckValidity(guest, kSecCSStrictValidate, requirement);
    SecStaticCodeRef code = NULL;
    if (status == errSecSuccess) status = SecCodeCopyStaticCode(guest, kSecCSDefaultFlags, &code);
    CFRelease(guest);
    if (status != errSecSuccess) return false;
    CFDataRef actual = code_identity(code); CFRelease(code);
    bool valid = actual != NULL && CFEqual(expected, actual);
    if (actual != NULL) CFRelease(actual);
    return valid;
}
// Bounded queues keep a non-reading UI from blocking supervision or cleanup.
typedef struct { unsigned char bytes[65536]; size_t length; } io_queue_t;
static bool relay_read(int fd, io_queue_t *queue)
{
    if (queue->length == sizeof(queue->bytes)) return false;
    ssize_t n = read(fd, queue->bytes + queue->length, sizeof(queue->bytes) - queue->length);
    if (n > 0) queue->length += (size_t)n;
    return n > 0 || (n < 0 && (errno == EAGAIN || errno == EINTR));
}
static bool relay_write(int fd, io_queue_t *queue)
{
    if (queue->length == 0) return true;
    ssize_t n = write(fd, queue->bytes, queue->length);
    if (n > 0) { queue->length -= (size_t)n; memmove(queue->bytes, queue->bytes + n, queue->length); }
    return n >= 0 || errno == EAGAIN || errno == EINTR;
}

static volatile sig_atomic_t stopping;
static void stop_handler(int number) { (void)number; stopping = 1; }
extern char **environ;

static int open_private_directory(const char *path)
{
    struct stat st;
    if (lstat(path, &st) != 0) return errno == ENOENT ? -3 : -1;
    if (!S_ISDIR(st.st_mode) || st.st_uid != getuid() || (st.st_mode & 077) != 0) return -1;
    int lock = openat(AT_FDCWD, path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (lock < 0) return -1;
    if (flock(lock, LOCK_EX | LOCK_NB) != 0) { int error = errno; close(lock); return error == EWOULDBLOCK ? -2 : -1; }
    return lock;
}

int main(int argc, char **argv)
{
    if (argc < 3) return 64;
    if (strcmp(argv[1], "--verify-claude") == 0) {
        int fd = -1; SecRequirementRef requirement = NULL;
        CFDataRef identity = verify_claude_file(argv[2], &fd, &requirement);
        if (identity != NULL) CFRelease(identity);
        if (requirement != NULL) CFRelease(requirement);
        if (fd >= 0) close(fd);
        return identity != NULL ? 0 : 77;
    }
    bool pty = strcmp(argv[1], "--pty") == 0 || strcmp(argv[1], "--claude-pty") == 0;
    bool claude = strcmp(argv[1], "--claude") == 0 || strcmp(argv[1], "--claude-pty") == 0;
    bool sweep = strcmp(argv[1], "--sweep") == 0;
    if (!sweep && (argc < 5 || (!pty && !claude && strcmp(argv[1], "--run") != 0))) return 64;
    const char *directory = argv[2];
    int directory_fd = open_private_directory(directory);
    if (directory_fd == -2) return 75;
    if (directory_fd == -3 && sweep) return 0;
    if (directory_fd < 0) return 77;
    char journal[PATH_MAX];
    if (snprintf(journal, sizeof(journal), "%s/processes", directory) >= (int)sizeof(journal)) return 64;
    if (sweep) {
        tracker_t recovery;
        tracker_init(&recovery, NULL);
        char metadata[PATH_MAX];
        snprintf(metadata, sizeof(metadata), "%s/session", directory);
        int metadata_fd = open(metadata, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
        if (metadata_fd < 0 && errno != ENOENT) return 77;
        if (metadata_fd >= 0) {
            struct stat st;
            if (fstat(metadata_fd, &st) != 0 || !S_ISREG(st.st_mode) || st.st_uid != getuid() || (st.st_mode & 077) != 0) return 77;
            FILE *file = fdopen(metadata_fd, "r");
            unsigned long long start;
            if (file == NULL || fscanf(file, "%63s %llu", recovery.token, &start) != 2) return 74;
            fclose(file);
            if (strncmp(recovery.token, TOKEN_NAME, strlen(TOKEN_NAME)) != 0 || strlen(recovery.token) != strlen(TOKEN_NAME) + 32) return 74;
            recovery.session_start_us = (uint64_t)start;
            tracker_orphan_scan(&recovery);
        }
        sweep_t result = sweep_journal(journal);
        // Orphans spawned before the first PID journal still carry the pre-recorded token.
        if (tracker_terminate(&recovery, 0, 3000, NULL) != 0) result.residual++;
        close(recovery.kq);
        if (result.residual == 0) {
            unlink(journal);
            unlink(metadata);
            char temporary[PATH_MAX];
            if (snprintf(temporary, sizeof(temporary), "%s.tmp", journal) < (int)sizeof(temporary)) unlink(temporary);
        }
        close(directory_fd);
        return result.residual == 0 ? 0 : 74;
    }
    int verified_fd = -1; SecRequirementRef requirement = NULL; CFDataRef identity = NULL;
    if (claude) {
        identity = verify_claude_file(argv[4], &verified_fd, &requirement);
        if (identity == NULL) return 77;
    }
    if (setsid() < 0) return 71;
    signal(SIGTTOU, SIG_IGN);
    int master = -1, slave = -1;
    if (pty) {
        struct winsize size = { .ws_row = 120, .ws_col = 400 };
        if (openpty(&master, &slave, NULL, NULL, &size) != 0 || ioctl(slave, TIOCSCTTY, 0) != 0) return 71;
        struct termios terminal;
        if (tcgetattr(slave, &terminal) != 0) return 71;
        terminal.c_lflag &= ~(ECHO | ECHONL);
        if (tcsetattr(slave, TCSANOW, &terminal) != 0) return 71;
        fcntl(master, F_SETFD, FD_CLOEXEC); fcntl(slave, F_SETFD, FD_CLOEXEC);
    }
    signal(SIGHUP, SIG_IGN);
    signal(SIGPIPE, SIG_IGN);
    signal(SIGTERM, stop_handler);
    signal(SIGINT, stop_handler);
    int control = socket(AF_UNIX, SOCK_STREAM, 0);
    if (control < 0) return 71;
    fcntl(control, F_SETFD, FD_CLOEXEC);
    struct sockaddr_un address = { .sun_family = AF_UNIX };
    if (strlen(argv[3]) >= sizeof(address.sun_path)) return 64;
    strcpy(address.sun_path, argv[3]);
    if (connect(control, (struct sockaddr *)&address, sizeof(address)) != 0) return 71;
    uid_t peer_uid; gid_t peer_gid;
    if (getpeereid(control, &peer_uid, &peer_gid) != 0 || peer_uid != getuid()) return 77;
    tracker_t tracker;
    tracker_init(&tracker, journal);
    char metadata[PATH_MAX];
    snprintf(metadata, sizeof(metadata), "%s/session", directory);
    int metadata_fd = open(metadata, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (metadata_fd < 0) return 74;
    if (dprintf(metadata_fd, "%s %llu\n", tracker.token, (unsigned long long)tracker.session_start_us) < 0 || fsync(metadata_fd) != 0) return 74;
    close(metadata_fd);
    size_t count = 0;
    while (environ[count] != NULL) count++;
    char **env = calloc(count + 2, sizeof(char *));
    if (env == NULL) return 71;
    size_t target = 0;
    for (size_t i = 0; i < count; i++)
        if (strncmp(environ[i], TOKEN_NAME, strlen(TOKEN_NAME)) != 0) env[target++] = environ[i];
    env[target] = tracker.token;
    posix_spawnattr_t attr;
    posix_spawn_file_actions_t actions;
    posix_spawnattr_init(&attr);
    posix_spawn_file_actions_init(&actions);
    sigset_t empty, defaults;
    sigemptyset(&empty); sigfillset(&defaults);
    posix_spawnattr_setsigmask(&attr, &empty);
    posix_spawnattr_setsigdefault(&attr, &defaults);
    posix_spawnattr_setpgroup(&attr, 0);
    posix_spawnattr_setflags(&attr, POSIX_SPAWN_START_SUSPENDED | POSIX_SPAWN_SETPGROUP |
        POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK);
    for (int fd = 0; fd <= 2; fd++) posix_spawn_file_actions_adddup2(&actions, pty ? slave : fd, fd);
    pid_t child = 0;
    const char *launch_path = argv[4];
#ifdef AIUSAGE_TEST_SIGNING
    // Test-only identity mismatch injection; never present in production binaries.
    const char *replacement = getenv("AIUSAGE_TEST_LAUNCH_PATH");
    if (replacement != NULL) launch_path = replacement;
#endif
    int error = posix_spawn(&child, launch_path, &actions, &attr, &argv[4], env);
    posix_spawn_file_actions_destroy(&actions);
    posix_spawnattr_destroy(&attr);
    free(env);
    if (error != 0) return 71;
    tracker_set_root(&tracker, child);
    // The durable identity journal and tracking registration precede SIGCONT.
    int result = 0;
    if (claude && !verify_claude_process(child, requirement, identity)) result = 77;
    if (identity != NULL) CFRelease(identity);
    if (requirement != NULL) CFRelease(requirement);
    if (verified_fd >= 0) close(verified_fd);
    if (pty && tcsetpgrp(slave, child) != 0) result = 71;
    if (slave >= 0) close(slave);
    io_queue_t to_child = { .length = 0 }, to_parent = { .length = 0 };
    if (pty) {
        fcntl(master, F_SETFL, O_NONBLOCK);
        fcntl(STDIN_FILENO, F_SETFL, O_NONBLOCK);
        fcntl(STDOUT_FILENO, F_SETFL, O_NONBLOCK);
    }
    struct pollfd poller = { .fd = control, .events = POLLIN | POLLHUP };
    if (result == 0 && (tracker.overflow || stopping || poll(&poller, 1, 0) != 0 || write(control, "R", 1) != 1)) result = 74;
    if (result == 0 && kill(child, SIGCONT) != 0) result = 71;
    while (result == 0 && !stopping && !tracker.root_reaped) {
        tracker_pump(&tracker, 20);
        if (tracker.escapes || tracker.overflow) { result = 74; break; }
        if (poll(&poller, 1, 0) != 0) break;
        if (pty) {
            if (!relay_read(master, &to_parent) || !relay_write(STDOUT_FILENO, &to_parent) ||
                !relay_read(STDIN_FILENO, &to_child) || !relay_write(master, &to_child)) { result = 74; break; }
        }
    }
    int residual = tracker_terminate(&tracker, 3000, 3000, NULL);
    if (residual != 0) result = 74;
    // Final outcome for the owner, sent after cleanup: 'E' a descendant left the group or
    // session (D12, the owner must fail closed), 'F' cleanup was not proven, 'C' all reaped.
    // The exit status cannot carry this because it relays the child's own status.
    char outcome = tracker.escapes ? 'E' : (residual != 0 || tracker.overflow ? 'F' : 'C');
    (void)write(control, &outcome, 1);
    if (result == 0 && tracker.root_reaped && WIFEXITED(tracker.root_status)) result = WEXITSTATUS(tracker.root_status);
    if (tracker_alive(&tracker) == 0) unlink(journal);
    if (master >= 0) close(master);
    close(tracker.kq);
    close(control);
    close(directory_fd);
    return result;
}
