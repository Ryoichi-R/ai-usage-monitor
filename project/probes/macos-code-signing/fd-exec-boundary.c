#define _DARWIN_C_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;

static int wait_for(pid_t pid, int *status)
{
    pid_t result;
    do { result = waitpid(pid, status, 0); } while (result < 0 && errno == EINTR);
    return result == pid ? 0 : -1;
}

int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--child") == 0) {
        fputs("unexpected: /dev/fd executable path launched the child\n", stderr);
        return 99;
    }
    if (argc != 1) return 64;

    int descriptor = open(argv[0], O_EXEC);
    if (descriptor < 0) {
        perror("open self with O_EXEC");
        return 65;
    }

    char fd_path[64];
    if (snprintf(fd_path, sizeof(fd_path), "/dev/fd/%d", descriptor) >= (int)sizeof(fd_path)) {
        fputs("/dev/fd path overflow\n", stderr);
        return 66;
    }
    char *child_arguments[] = { argv[0], "--child", NULL };

    pid_t spawned = -1;
    int spawn_result = posix_spawn(&spawned, fd_path, NULL, NULL, child_arguments, environ);
    if (spawn_result == 0) {
        int status = 0;
        if (wait_for(spawned, &status) != 0) {
            perror("waitpid after posix_spawn");
            return 67;
        }
        fprintf(stderr, "posix_spawn unexpectedly accepted /dev/fd path (status=%d)\n", status);
        return 68;
    }
    printf("posix_spawn_fd_path_errno=%d (%s)\n", spawn_result, strerror(spawn_result));
    if (spawn_result != EACCES) return 69;

    pid_t forked = fork();
    if (forked < 0) {
        perror("fork");
        return 70;
    }
    if (forked == 0) {
        execv(fd_path, child_arguments);
        _exit(errno == EACCES ? 126 : 127);
    }
    int status = 0;
    if (wait_for(forked, &status) != 0) {
        perror("waitpid after fork+execv");
        return 71;
    }
    if (!WIFEXITED(status) || WEXITSTATUS(status) != 126) {
        fprintf(stderr, "fork+execv did not fail with EACCES (status=%d)\n", status);
        return 72;
    }
    puts("fork_execv_fd_path_errno=13 (Permission denied)");
    puts("RESULT: opened executable FD could not be used for execution via /dev/fd on this macOS host");
    close(descriptor);
    return 0;
}
