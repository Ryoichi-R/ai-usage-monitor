#define _DARWIN_C_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>
#include <util.h>

extern char **environ;

static char self_path[PATH_MAX];

static void fail(const char *message)
{
    perror(message);
    exit(2);
}

static void write_all(int fd, const char *text, size_t length)
{
    size_t offset = 0;
    while (offset < length) {
        ssize_t written = write(fd, text + offset, length - offset);
        if (written < 0 && errno == EINTR) continue;
        if (written <= 0) fail("write");
        offset += (size_t)written;
    }
}

static void write_line(int fd, const char *text)
{
    write_all(fd, text, strlen(text));
    write_all(fd, "\n", 1);
}

static bool write_all_or_closed(int fd, const char *text, size_t length)
{
    size_t offset = 0;
    while (offset < length) {
        ssize_t written = write(fd, text + offset, length - offset);
        if (written < 0 && errno == EINTR) continue;
        if (written < 0 && errno == EPIPE) return false;
        if (written <= 0) fail("write state");
        offset += (size_t)written;
    }
    return true;
}

static long long monotonic_ms(void)
{
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) fail("clock_gettime");
    return ((long long)now.tv_sec * 1000LL) + (now.tv_nsec / 1000000LL);
}

static void sleep_ms(long milliseconds)
{
    struct timespec duration = { .tv_sec = milliseconds / 1000,
                                 .tv_nsec = (milliseconds % 1000) * 1000000L };
    while (nanosleep(&duration, &duration) != 0 && errno == EINTR) { }
}

static int read_line_timeout(int fd, char *buffer, size_t capacity, int timeout_ms)
{
    size_t used = 0;
    long long deadline = monotonic_ms() + timeout_ms;
    if (capacity < 2) return -1;
    while (used + 1 < capacity) {
        long long remaining = deadline - monotonic_ms();
        if (remaining <= 0) return -1;
        struct pollfd item = { .fd = fd, .events = POLLIN | POLLHUP };
        int ready = poll(&item, 1, (int)remaining);
        if (ready < 0 && errno == EINTR) continue;
        if (ready <= 0) return -1;
        char value;
        ssize_t count = read(fd, &value, 1);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return -1;
        if (value == '\n') {
            buffer[used] = '\0';
            return (int)used;
        }
        buffer[used++] = value;
    }
    return -1;
}

static void close_if_open(int fd)
{
    if (fd >= 0) (void)close(fd);
}

static void ignore_termination_signals(void)
{
    (void)signal(SIGTERM, SIG_IGN);
    (void)signal(SIGHUP, SIG_IGN);
}

static int fake_main(int argc, char **argv)
{
    if (argc != 5) return 2;
    const char *pid_path = argv[2];
    int report_fd = atoi(argv[3]);
    int master_check_fd = atoi(argv[4]);
    ignore_termination_signals();

    pid_t grandchild = fork();
    if (grandchild < 0) fail("fake fork");
    if (grandchild == 0) {
        ignore_termination_signals();
        close_if_open(report_fd);
        for (;;) pause();
    }

    struct winsize size = { 0 };
    (void)ioctl(STDIN_FILENO, TIOCGWINSZ, &size);
    struct termios settings = { 0 };
    bool have_termios = tcgetattr(STDIN_FILENO, &settings) == 0;
    int life_fd_open = fcntl(3, F_GETFD) >= 0;
    int master_fd_open = master_check_fd >= 0 && fcntl(master_check_fd, F_GETFD) >= 0;
    pid_t foreground = tcgetpgrp(STDIN_FILENO);
    pid_t terminal_session = tcgetsid(STDIN_FILENO);
    char report[768];
    int length = snprintf(report, sizeof(report),
        "pid=%d grandchild=%d pgid=%d sid=%d tty=%d fg=%d tty_sid=%d rows=%u cols=%u echo=%d canonical=%d life_fd_open=%d master_fd_open=%d",
        getpid(), grandchild, getpgrp(), getsid(0), isatty(STDIN_FILENO), foreground,
        terminal_session, (unsigned)size.ws_row, (unsigned)size.ws_col,
        have_termios && (settings.c_lflag & ECHO) != 0,
        have_termios && (settings.c_lflag & ICANON) != 0,
        life_fd_open, master_fd_open);
    if (length < 0 || (size_t)length >= sizeof(report)) fail("format report");

    int file = open(pid_path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (file < 0) fail("open pid report");
    write_all(file, report, (size_t)length);
    write_all(file, "\n", 1);
    close(file);
    write_all(report_fd, report, (size_t)length);
    write_all(report_fd, "\n", 1);
    close(report_fd);
    for (;;) pause();
}

static bool group_alive(pid_t pgid)
{
    if (kill(-pgid, 0) == 0) return true;
    return errno == EPERM;
}

static bool terminate_group_and_wait(pid_t pgid)
{
    (void)kill(-pgid, SIGTERM);
    sleep_ms(250);
    (void)kill(-pgid, SIGKILL);
    long long deadline = monotonic_ms() + 3000;
    while (group_alive(pgid) && monotonic_ms() < deadline) sleep_ms(20);
    return !group_alive(pgid);
}

static bool cleanup_group(pid_t pgid, pid_t child, bool *direct_reaped)
{
    (void)kill(-pgid, SIGTERM);
    sleep_ms(250);
    (void)kill(-pgid, SIGKILL);

    int status = 0;
    pid_t waited;
    do { waited = waitpid(child, &status, 0); } while (waited < 0 && errno == EINTR);
    *direct_reaped = waited == child;

    long long deadline = monotonic_ms() + 3000;
    while (group_alive(pgid) && monotonic_ms() < deadline) sleep_ms(20);
    return !group_alive(pgid);
}

static int add_close_action(posix_spawn_file_actions_t *actions, int fd)
{
    return posix_spawn_file_actions_addclose(actions, fd);
}

static pid_t launch_stdio(const char *pid_path, int report_pipe[2])
{
    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attributes;
    if (posix_spawn_file_actions_init(&actions) != 0) fail("spawn actions init");
    if (posix_spawnattr_init(&attributes) != 0) fail("spawn attr init");
    if (posix_spawn_file_actions_adddup2(&actions, report_pipe[1], 7) != 0) fail("spawn report dup");
    int close_fds[] = { report_pipe[0], report_pipe[1], 3, 4, 5, 6 };
    for (size_t i = 0; i < sizeof(close_fds) / sizeof(close_fds[0]); i++) {
        int fd = close_fds[i];
        bool seen = fd == 7;
        for (size_t previous = 0; previous < i; previous++) {
            if (close_fds[previous] == fd) seen = true;
        }
        if (!seen && add_close_action(&actions, fd) != 0) fail("spawn close");
    }
    short flags = POSIX_SPAWN_SETPGROUP;
    if (posix_spawnattr_setflags(&attributes, flags) != 0) fail("spawn flags");
    if (posix_spawnattr_setpgroup(&attributes, 0) != 0) fail("spawn pgroup");
    char report_arg[] = "7";
    char check_arg[] = "-1";
    char *arguments[] = { self_path, "--fake", (char *)pid_path, report_arg, check_arg, NULL };
    pid_t child = 0;
    int result = posix_spawn(&child, self_path, &actions, &attributes, arguments, environ);
    posix_spawn_file_actions_destroy(&actions);
    posix_spawnattr_destroy(&attributes);
    if (result != 0) { errno = result; fail("posix_spawn fake"); }
    return child;
}

static pid_t launch_pty(const char *pid_path, int report_pipe[2], int *master_fd)
{
    int master = -1, slave = -1;
    if (openpty(&master, &slave, NULL, NULL, NULL) != 0) fail("openpty");
    int isolated_master = fcntl(master, F_DUPFD, 15);
    if (isolated_master < 0) fail("duplicate PTY master");
    close(master);
    master = isolated_master;

    struct winsize size = { .ws_row = 120, .ws_col = 400 };
    if (ioctl(master, TIOCSWINSZ, &size) != 0) fail("set PTY winsize");
    pid_t child = fork();
    if (child < 0) fail("PTY child fork");
    if (child == 0) {
        close(master);
        close(report_pipe[0]);
        if (dup2(report_pipe[1], 7) < 0) _exit(120);
        if (report_pipe[1] != 7) close_if_open(report_pipe[1]);
        close_if_open(3);
        close_if_open(4);
        close_if_open(5);
        close_if_open(6);
        if (setsid() < 0) _exit(121);
        if (ioctl(slave, TIOCSCTTY, 0) < 0) _exit(122);
        if (dup2(slave, STDIN_FILENO) < 0 || dup2(slave, STDOUT_FILENO) < 0 || dup2(slave, STDERR_FILENO) < 0) _exit(123);
        if (slave > STDERR_FILENO) close(slave);
        if (tcsetpgrp(STDIN_FILENO, getpgrp()) != 0) _exit(124);
        char report_arg[] = "7";
        char master_arg[24];
        (void)snprintf(master_arg, sizeof(master_arg), "%d", master);
        char *arguments[] = { self_path, "--fake", (char *)pid_path, report_arg, master_arg, NULL };
        execv(self_path, arguments);
        _exit(125);
    }
    close(slave);
    *master_fd = master;
    return child;
}

static void finish_supervisor(const char *done_path, pid_t pgid, pid_t child)
{
    bool direct_reaped = false;
    bool gone = cleanup_group(pgid, child, &direct_reaped);
    char result[256];
    (void)snprintf(result, sizeof(result), "DONE group_gone=%d direct_child_reaped=%d term_sent=1 kill_sent=1\n", gone, direct_reaped);
    int file = open(done_path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (file < 0) fail("open cleanup result");
    write_all(file, result, strlen(result));
    close(file);
}

static int supervisor_main(int argc, char **argv)
{
    if (argc != 6) return 2;
    const char *mode = argv[2];
    const char *pid_path = argv[3];
    const char *done_path = argv[4];
    int ready_delay_ms = atoi(argv[5]);
    (void)signal(SIGPIPE, SIG_IGN);
    if (ready_delay_ms > 0) {
        struct pollfd lifetime = { .fd = 3, .events = POLLIN | POLLHUP };
        int result = poll(&lifetime, 1, ready_delay_ms);
        if (result > 0) {
            char value;
            if (read(3, &value, 1) <= 0) {
                int file = open(done_path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
                if (file < 0) fail("open pre-ready result");
                write_line(file, "DONE no_child=1 ready=0 group_gone=1");
                close(file);
                return 0;
            }
        }
        if (result < 0 && errno != EINTR) fail("pre-ready poll");
    }
    if (write(5, "READY\n", 6) != 6) return 3;
    bool active = false;
    pid_t child = -1, pgid = -1;
    int master = -1;

    for (;;) {
        struct pollfd descriptors[2] = {
            { .fd = 3, .events = POLLIN | POLLHUP },
            { .fd = 4, .events = POLLIN | POLLHUP }
        };
        int result = poll(descriptors, 2, -1);
        if (result < 0 && errno == EINTR) continue;
        if (result < 0) fail("supervisor poll");
        if (descriptors[0].revents != 0) {
            char value;
            ssize_t count = read(3, &value, 1);
            if (count <= 0) {
                if (active) finish_supervisor(done_path, pgid, child);
                else {
                    int file = open(done_path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
                    if (file < 0) fail("open no-child result");
                    write_line(file, "DONE no_child=1 ready=1 group_gone=1");
                    close(file);
                }
                close_if_open(master);
                return 0;
            }
        }
        if (descriptors[1].revents != 0) {
            char command;
            ssize_t count = read(4, &command, 1);
            if (count <= 0) continue;
            if (command == 'L' && !active) {
                int report_pipe[2];
                if (pipe(report_pipe) != 0) fail("fake report pipe");
                child = strcmp(mode, "pty") == 0
                    ? launch_pty(pid_path, report_pipe, &master)
                    : launch_stdio(pid_path, report_pipe);
                close(report_pipe[1]);
                char report[768];
                if (read_line_timeout(report_pipe[0], report, sizeof(report), 3000) < 0) fail("fake report timeout");
                close(report_pipe[0]);
                pgid = strcmp(mode, "pty") == 0 ? child : getpgid(child);
                char state[1024];
                (void)snprintf(state, sizeof(state), "INFO %s\nSTARTED pid=%d pgid=%d\n", report, child, pgid);
                active = true;
                if (!write_all_or_closed(5, state, strlen(state))) {
                    finish_supervisor(done_path, pgid, child);
                    close_if_open(master);
                    return 0;
                }
            } else if (command == 'C') {
                if (active) finish_supervisor(done_path, pgid, child);
                close_if_open(master);
                return 0;
            }
        }
    }
}

static int app_main(int argc, char **argv)
{
    if (argc != 7) return 2;
    const char *mode = argv[2];
    const char *pid_path = argv[3];
    const char *done_path = argv[4];
    const char *scenario = argv[5];
    int runner_fd = atoi(argv[6]);
    int life[2], control[2], state[2];
    if (pipe(life) != 0 || pipe(control) != 0 || pipe(state) != 0) fail("app pipes");
    pid_t helper = fork();
    if (helper < 0) fail("helper fork");
    if (helper == 0) {
        int safe_life = fcntl(life[0], F_DUPFD, 20);
        int safe_control = fcntl(control[0], F_DUPFD, 20);
        int safe_state = fcntl(state[1], F_DUPFD, 20);
        if (safe_life < 0 || safe_control < 0 || safe_state < 0) _exit(109);
        close_if_open(life[0]); close_if_open(life[1]);
        close_if_open(control[0]); close_if_open(control[1]);
        close_if_open(state[0]); close_if_open(state[1]);
        close_if_open(runner_fd);
        if (dup2(safe_life, 3) < 0 || dup2(safe_control, 4) < 0 || dup2(safe_state, 5) < 0) _exit(110);
        close_if_open(safe_life); close_if_open(safe_control); close_if_open(safe_state);
        char delay_arg[8] = "0";
        if (strcmp(scenario, "before-ready") == 0) strcpy(delay_arg, "1000");
        char *arguments[] = { self_path, "--supervisor", (char *)mode, (char *)pid_path, (char *)done_path, delay_arg, NULL };
        execv(self_path, arguments);
        _exit(111);
    }
    close(life[0]); close(control[0]); close(state[1]);
    char state_line[1200];
    char message[1200];
    if (strcmp(scenario, "before-ready") == 0) {
        (void)snprintf(message, sizeof(message), "SPAWNED helper=%d\n", helper);
        write_all(runner_fd, message, strlen(message));
    }
    if (read_line_timeout(state[0], state_line, sizeof(state_line), 5000) < 0 || strcmp(state_line, "READY") != 0) return 4;
    if (strcmp(scenario, "before-ready") != 0) {
        (void)snprintf(message, sizeof(message), "READY helper=%d\n", helper);
        write_all(runner_fd, message, strlen(message));
    }
    bool launches_cli = strcmp(scenario, "before-start") != 0 && strcmp(scenario, "before-ready") != 0;
    if (!launches_cli) for (;;) pause();

    write(control[1], "L", 1);
    if (read_line_timeout(state[0], state_line, sizeof(state_line), 5000) < 0) { fprintf(stderr, "APP missing INFO line\n"); return 5; }
    (void)snprintf(message, sizeof(message), "%s\n", state_line);
    write_all(runner_fd, message, strlen(message));
    if (read_line_timeout(state[0], state_line, sizeof(state_line), 5000) < 0) { fprintf(stderr, "APP missing STARTED line\n"); return 6; }
    (void)snprintf(message, sizeof(message), "%s\n", state_line);
    write_all(runner_fd, message, strlen(message));
    int child = -1, pgid = -1;
    if (sscanf(state_line, "STARTED pid=%d pgid=%d", &child, &pgid) != 2 || child <= 0 || pgid <= 0) return 7;

    if (strcmp(scenario, "cancel") == 0 || strcmp(scenario, "timeout") == 0) {
        if (strcmp(scenario, "timeout") == 0) sleep_ms(50);
        if (write(control[1], "C", 1) != 1) return 8;
        int helper_status = 0;
        pid_t waited;
        do { waited = waitpid(helper, &helper_status, 0); } while (waited < 0 && errno == EINTR);
        close_if_open(state[0]); close_if_open(control[1]); close_if_open(life[1]);
        return waited == helper && WIFEXITED(helper_status) && WEXITSTATUS(helper_status) == 0 ? 0 : 9;
    }

    if (strcmp(scenario, "helper-crash") == 0) {
        int helper_status = 0;
        pid_t waited;
        do {
            waited = waitpid(helper, &helper_status, WNOHANG);
            if (waited == 0) sleep_ms(20);
        } while (waited == 0 || (waited < 0 && errno == EINTR));
        if (waited != helper || !WIFSIGNALED(helper_status)) return 10;
        bool recovered = terminate_group_and_wait((pid_t)pgid);
        int file = open(done_path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
        if (file < 0) fail("open helper-crash recovery result");
        char recovery[256];
        (void)snprintf(recovery, sizeof(recovery), "DONE helper_failed=1 parent_recovered=1 group_gone=%d direct_child_reaped=unknown\n", recovered);
        write_all(file, recovery, strlen(recovery));
        close(file);
        close_if_open(state[0]); close_if_open(control[1]); close_if_open(life[1]);
        return recovered ? 0 : 11;
    }

    for (;;) pause();
}

static bool wait_for_path(const char *path, int timeout_ms, char *contents, size_t capacity)
{
    long long deadline = monotonic_ms() + timeout_ms;
    while (monotonic_ms() < deadline) {
        int file = open(path, O_RDONLY);
        if (file >= 0) {
            ssize_t count = read(file, contents, capacity - 1);
            close(file);
            if (count > 0) { contents[count] = '\0'; return true; }
        }
        sleep_ms(20);
    }
    return false;
}

static bool wait_for_child(pid_t pid, int timeout_ms, int *status)
{
    long long deadline = monotonic_ms() + timeout_ms;
    while (monotonic_ms() < deadline) {
        pid_t result = waitpid(pid, status, WNOHANG);
        if (result == pid) return true;
        if (result < 0 && errno != EINTR) return false;
        sleep_ms(20);
    }
    return false;
}

static bool wait_for_process_exit(pid_t pid, int timeout_ms)
{
    long long deadline = monotonic_ms() + timeout_ms;
    while (monotonic_ms() < deadline) {
        if (kill(pid, 0) != 0 && errno == ESRCH) return true;
        sleep_ms(20);
    }
    return kill(pid, 0) != 0 && errno == ESRCH;
}

static int run_scenario(const char *directory, const char *mode, const char *scenario)
{
    bool before_ready = strcmp(scenario, "before-ready") == 0;
    bool before_start = strcmp(scenario, "before-start") == 0;
    bool start_child = !before_ready && !before_start;
    bool both_owners_killed = strcmp(scenario, "both-killed") == 0;
    bool parent_killed = strcmp(scenario, "before-ready") == 0 || strcmp(scenario, "before-start") == 0 || strcmp(scenario, "started") == 0 || both_owners_killed;
    char pid_path[PATH_MAX], done_path[PATH_MAX];
    (void)snprintf(pid_path, sizeof(pid_path), "%s/%s-%s-pids.txt", directory, mode, scenario);
    (void)snprintf(done_path, sizeof(done_path), "%s/%s-%s-done.txt", directory, mode, scenario);
    if (unlink(pid_path) != 0 && errno != ENOENT) fail("remove stale fake pid report");
    if (unlink(done_path) != 0 && errno != ENOENT) fail("remove stale cleanup result");
    int status_pipe[2];
    if (pipe(status_pipe) != 0) fail("runner pipe");
    pid_t app = fork();
    if (app < 0) fail("app fork");
    if (app == 0) {
        close(status_pipe[0]);
        (void)fcntl(status_pipe[1], F_SETFD, 0);
        char fd_arg[24];
        (void)snprintf(fd_arg, sizeof(fd_arg), "%d", status_pipe[1]);
        execl(self_path, self_path, "--app", mode, pid_path, done_path,
              (char *)scenario, fd_arg, (char *)NULL);
        _exit(112);
    }
    close(status_pipe[1]);
    char line[1200];
    if (read_line_timeout(status_pipe[0], line, sizeof(line), 7000) < 0) fail("app ready timeout");
    int helper = -1;
    const char *helper_prefix = before_ready ? "SPAWNED helper=%d" : "READY helper=%d";
    if (sscanf(line, helper_prefix, &helper) != 1) { fprintf(stderr, "bad helper status: %s\n", line); return 10; }
    pid_t child_pid = -1, pgid = -1;
    char child_report[1024] = "";
    if (start_child) {
        if (read_line_timeout(status_pipe[0], line, sizeof(line), 7000) < 0 || strncmp(line, "INFO ", 5) != 0) fail("fake info timeout");
        (void)snprintf(child_report, sizeof(child_report), "%s", line + 5);
        int line_length = read_line_timeout(status_pipe[0], line, sizeof(line), 7000);
        if (line_length < 0 || sscanf(line, "STARTED pid=%d pgid=%d", &child_pid, &pgid) != 2) fail("fake start timeout");
    }
    if (both_owners_killed) {
        if (kill((pid_t)helper, SIGKILL) != 0) fail("kill helper in simultaneous-death scenario");
        if (kill(app, SIGKILL) != 0) fail("kill app in simultaneous-death scenario");
    }
    if (strcmp(scenario, "helper-crash") == 0 && kill((pid_t)helper, SIGKILL) != 0) fail("kill supervisor helper");
    if (parent_killed && kill(app, SIGKILL) != 0) fail("kill app parent");
    int app_status = 0;
    if (!wait_for_child(app, 7000, &app_status)) { fprintf(stderr, "app did not exit: %d\n", app); return 11; }
    if (parent_killed && (!WIFSIGNALED(app_status) || WTERMSIG(app_status) != SIGKILL)) return 12;
    if (!parent_killed && (!WIFEXITED(app_status) || WEXITSTATUS(app_status) != 0)) {
        fprintf(stderr, "app recovery/termination failed status=%d\n", app_status);
        return 13;
    }

    char cleanup[512];
    if (both_owners_killed) {
        bool helper_gone = wait_for_process_exit((pid_t)helper, 3000);
        bool group_survived = group_alive(pgid);
        bool externally_recovered = terminate_group_and_wait((pid_t)pgid);
        (void)snprintf(cleanup, sizeof(cleanup),
            "DONE both_owners_killed=1 group_alive_before_external_cleanup=%d external_cleanup=1 group_gone=%d direct_child_reaped=unknown\n",
            group_survived, externally_recovered);
        if (!group_survived || !helper_gone || !externally_recovered) {
            (void)kill(-(pid_t)pgid, SIGKILL);
            fprintf(stderr, "simultaneous-death boundary inconclusive helper_gone=%d group_survived=%d recovered=%d\n",
                    helper_gone, group_survived, externally_recovered);
            return 22;
        }
        printf("DETECTED residual_after_both_owners_SIGKILL=1 fallback_runner_recovered=1\n");
    } else if (!wait_for_path(done_path, 5000, cleanup, sizeof(cleanup))) {
        fprintf(stderr, "cleanup timeout: %s\n", done_path);
        return 12;
    }
    if (strcmp(scenario, "helper-crash") != 0 && !wait_for_process_exit((pid_t)helper, 3000)) { fprintf(stderr, "supervisor remains: %d\n", helper); return 14; }
    if (strstr(cleanup, "group_gone=1") == NULL) { fprintf(stderr, "cleanup did not confirm group removal: %s", cleanup); return 15; }
    if (strcmp(scenario, "helper-crash") == 0 && strstr(cleanup, "parent_recovered=1") == NULL) { fprintf(stderr, "parent did not record helper crash recovery: %s", cleanup); return 16; }
    if (start_child) {
        int fake_pid = -1, grandchild = -1, fake_pgid = -1, sid = -1, tty = -1;
        int foreground = -1, terminal_sid = -1, echo = -1, canonical = -1;
        int life_fd_open = -1, master_fd_open = -1;
        unsigned rows = 0, columns = 0;
        int parsed = sscanf(child_report,
            "pid=%d grandchild=%d pgid=%d sid=%d tty=%d fg=%d tty_sid=%d rows=%u cols=%u echo=%d canonical=%d life_fd_open=%d master_fd_open=%d",
            &fake_pid, &grandchild, &fake_pgid, &sid, &tty, &foreground, &terminal_sid,
            &rows, &columns, &echo, &canonical, &life_fd_open, &master_fd_open);
        if (parsed != 13 || fake_pid <= 0 || grandchild <= 0 || fake_pgid != fake_pid || life_fd_open != 0 || master_fd_open != 0) {
            fprintf(stderr, "fake process/FD contract failed: %s\n", child_report);
            return 15;
        }
        if (strcmp(scenario, "helper-crash") == 0 || both_owners_killed) {
            if (strstr(cleanup, "direct_child_reaped=unknown") == NULL) { fprintf(stderr, "orphan reaping limit was not recorded: %s", cleanup); return 17; }
        } else if (strstr(cleanup, "direct_child_reaped=1") == NULL) {
            fprintf(stderr, "direct child was not reaped: %s", cleanup);
            return 18;
        }
        if (strcmp(mode, "pty") == 0 && !(tty == 1 && sid == fake_pid && foreground == fake_pgid && terminal_sid == sid && rows == 120 && columns == 400 && echo == 1 && canonical == 1)) {
            fprintf(stderr, "PTY session/terminal contract failed: %s\n", child_report);
            return 17;
        }
        if (strcmp(mode, "stdio") == 0 && tty != 0) { fprintf(stderr, "stdio fake unexpectedly has a terminal: %s\n", child_report); return 18; }
    } else if (access(pid_path, F_OK) == 0) {
        fprintf(stderr, "fake CLI started before parent request\n");
        return 19;
    }
    if (before_ready && strstr(cleanup, "ready=0") == NULL) { fprintf(stderr, "helper unexpectedly reached READY: %s", cleanup); return 20; }
    if (start_child && (pgid <= 0 || group_alive(pgid))) { fprintf(stderr, "process group remains: %d\n", pgid); return 21; }
    const char *label = before_ready ? "parent-SIGKILL-before-helper-ready"
        : (before_start ? "parent-SIGKILL-after-ready-before-start"
        : (both_owners_killed ? "parent-and-helper-SIGKILL-after-start"
        : (parent_killed ? "parent-SIGKILL-after-start" : scenario)));
    printf("PASS mode=%s scenario=%s cleanup=%s", mode, label, cleanup);
    if (start_child) printf(" contract=ok");
    putchar('\n');
    close(status_pipe[0]);
    return 0;
}

static int detached_descendant_probe(void)
{
    int ready[2];
    if (pipe(ready) != 0) fail("detached probe pipe");
    pid_t child = fork();
    if (child < 0) fail("detached probe fork");
    if (child == 0) {
        close(ready[0]);
        if (setpgid(0, 0) != 0) _exit(130);
        ignore_termination_signals();
        pid_t detached = fork();
        if (detached < 0) _exit(131);
        if (detached == 0) {
            close_if_open(ready[0]);
            if (setsid() < 0) _exit(132);
            ignore_termination_signals();
            char report[96];
            int length = snprintf(report, sizeof(report), "detached pid=%d pgid=%d\n", getpid(), getpgrp());
            if (length < 0 || (size_t)length >= sizeof(report)) _exit(133);
            write_all(ready[1], report, (size_t)length);
            close(ready[1]);
            for (;;) pause();
        }
        char report[96];
        int length = snprintf(report, sizeof(report), "child pid=%d pgid=%d\n", getpid(), getpgrp());
        if (length < 0 || (size_t)length >= sizeof(report)) _exit(134);
        write_all(ready[1], report, (size_t)length);
        close(ready[1]);
        for (;;) pause();
    }

    close(ready[1]);
    char line[128];
    pid_t group = -1, escaped = -1, escaped_group = -1;
    bool child_seen = false, detached_seen = false;
    long long report_deadline = monotonic_ms() + 3000;
    while ((!child_seen || !detached_seen) && monotonic_ms() < report_deadline) {
        if (read_line_timeout(ready[0], line, sizeof(line), 500) < 0) continue;
        if (sscanf(line, "child pid=%*d pgid=%d", &group) == 1) child_seen = true;
        else if (sscanf(line, "detached pid=%d pgid=%d", &escaped, &escaped_group) == 2) detached_seen = true;
        else {
            fprintf(stderr, "unexpected detached probe report: %s\n", line);
            if (group > 0) (void)kill(-group, SIGKILL);
            if (escaped > 0) (void)kill(escaped, SIGKILL);
            return 30;
        }
    }
    if (!child_seen || !detached_seen) {
        fprintf(stderr, "detached probe reports incomplete child=%d descendant=%d\n", child_seen, detached_seen);
        if (group > 0) (void)kill(-group, SIGKILL);
        if (escaped > 0) (void)kill(escaped, SIGKILL);
        return 30;
    }
    close(ready[0]);
    if (group != child || escaped <= 0 || escaped_group != escaped || escaped == child) return 30;

    (void)kill(-group, SIGTERM);
    sleep_ms(250);
    (void)kill(-group, SIGKILL);
    int status = 0;
    pid_t waited;
    do { waited = waitpid(child, &status, 0); } while (waited < 0 && errno == EINTR);
    long long deadline = monotonic_ms() + 3000;
    while (group_alive(group) && monotonic_ms() < deadline) sleep_ms(20);
    bool group_gone = !group_alive(group);
    bool escaped_alive = kill(escaped, 0) == 0 || errno == EPERM;
    if (!group_gone || !escaped_alive || waited != child) {
        if (escaped_alive) (void)kill(escaped, SIGKILL);
        fprintf(stderr, "detached probe inconclusive group_gone=%d escaped_alive=%d reaped=%d\\n", group_gone, escaped_alive, waited == child);
        return 31;
    }

    printf("DETECTED expected-no-go=detached-descendant-not-contained group_gone=1 escaped_alive=1 escaped_pgid=%d\n", escaped_group);
    (void)kill(escaped, SIGKILL);
    deadline = monotonic_ms() + 3000;
    while ((kill(escaped, 0) == 0 || errno == EPERM) && monotonic_ms() < deadline) sleep_ms(20);
    if (kill(escaped, 0) == 0 || errno == EPERM) {
        fprintf(stderr, "detached fake descendant remains after probe cleanup: %d\\n", escaped);
        return 32;
    }
    return 0;
}

static int repeated_cleanup_trials(const char *directory, int repetitions)
{
    const char *modes[] = { "stdio", "pty" };
    const char *scenarios[] = { "cancel", "timeout" };
    for (size_t mode = 0; mode < sizeof(modes) / sizeof(modes[0]); mode++) {
        for (size_t scenario = 0; scenario < sizeof(scenarios) / sizeof(scenarios[0]); scenario++) {
            for (int iteration = 0; iteration < repetitions; iteration++) {
                int result = run_scenario(directory, modes[mode], scenarios[scenario]);
                if (result != 0) {
                    fprintf(stderr, "repeat cleanup failed mode=%s scenario=%s iteration=%d\n",
                            modes[mode], scenarios[scenario], iteration + 1);
                    return result;
                }
            }
        }
    }
    printf("PASS repeated_cleanup_trials=%d\n", repetitions * 4);
    return 0;
}

static int runner_main(void)
{
    char directory[] = "/private/tmp/ai-usage-p0-9-XXXXXX";
    if (mkdtemp(directory) == NULL) fail("mkdtemp");
    printf("result_dir=%s\n", directory);
    int result = run_scenario(directory, "stdio", "before-ready");
    if (result == 0) result = run_scenario(directory, "stdio", "before-start");
    if (result == 0) result = run_scenario(directory, "stdio", "started");
    if (result == 0) result = run_scenario(directory, "stdio", "cancel");
    if (result == 0) result = run_scenario(directory, "stdio", "timeout");
    if (result == 0) result = run_scenario(directory, "stdio", "helper-crash");
    if (result == 0) result = run_scenario(directory, "stdio", "both-killed");
    if (result == 0) result = run_scenario(directory, "pty", "before-ready");
    if (result == 0) result = run_scenario(directory, "pty", "before-start");
    if (result == 0) result = run_scenario(directory, "pty", "started");
    if (result == 0) result = run_scenario(directory, "pty", "cancel");
    if (result == 0) result = run_scenario(directory, "pty", "timeout");
    if (result == 0) result = run_scenario(directory, "pty", "helper-crash");
    if (result == 0) result = run_scenario(directory, "pty", "both-killed");
    if (result == 0) result = repeated_cleanup_trials(directory, 20);
    if (result == 0) result = detached_descendant_probe();
    return result;
}

int main(int argc, char **argv)
{
    if (realpath(argv[0], self_path) == NULL) fail("resolve probe path");
    if (argc > 1 && strcmp(argv[1], "--fake") == 0) return fake_main(argc, argv);
    if (argc > 1 && strcmp(argv[1], "--supervisor") == 0) return supervisor_main(argc, argv);
    if (argc > 1 && strcmp(argv[1], "--app") == 0) return app_main(argc, argv);
    return runner_main();
}
