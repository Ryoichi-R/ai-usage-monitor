#include <sys/stat.h>
#include <sys/file.h>
#include <sys/socket.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>

// Return 0 only for an owned object with the expected type and no group/other access.
int aiusage_validate(const char *path, int directory)
{
    struct stat value;
    if (lstat(path, &value) != 0) return errno == ENOENT ? 2 : 1;
    return value.st_uid == getuid() && (value.st_mode & 077) == 0 &&
        (directory ? S_ISDIR(value.st_mode) : S_ISSOCK(value.st_mode)) ? 0 : 1;
}
int aiusage_lock_directory(const char *path)
{
    if (aiusage_validate(path, 1) != 0) return -1;
    int fd = open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0) return -1;
    struct stat value;
    if (fstat(fd, &value) != 0 || value.st_uid != getuid() || (value.st_mode & 077) != 0 ||
        flock(fd, LOCK_EX | LOCK_NB) != 0) { close(fd); return -1; }
    return fd;
}
int aiusage_peer(int fd)
{
    uid_t uid; gid_t gid;
    return getpeereid(fd, &uid, &gid) == 0 && uid == getuid() ? 0 : 1;
}
