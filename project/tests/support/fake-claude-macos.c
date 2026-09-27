#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <termios.h>
#include <stdlib.h>
#include <time.h>
int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--version") == 0) { puts("2.1.274 (Claude Code)"); return 0; }
    if (argc == 2 && strcmp(argv[1], "--help") == 0) {
        puts("--setting-sources --settings --tools --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader"); return 0;
    }
    if (argc == 3 && strcmp(argv[1], "--marker") == 0) { FILE *f = fopen(argv[2], "w"); if (!f) return 2; fputs("fake", f); fclose(f); return 0; }
    struct termios mode;
    if (tcgetattr(0, &mode) != 0) return 2;
    cfmakeraw(&mode); if (tcsetattr(0, TCSANOW, &mode) != 0) return 2;
    printf("[Screen Reader Mode: on via flag]\r\n$\r\n"); fflush(stdout);
    char command[64] = {0}; size_t length = 0;
    for (;;) {
        char ch; if (read(0, &ch, 1) != 1) return 0;
        if (ch == '\033') return 0;
        if (length >= sizeof(command) - 1) return 3;
        command[length++] = ch;
        if (ch == '\r') {
            if (strcmp(command, "/usage\r") != 0) return 4;
            time_t session = time(NULL) + 3600 + 9 * 3600, week = time(NULL) + 86400 + 9 * 3600;
            char session_reset[128], week_reset[128];
            strftime(session_reset, sizeof(session_reset), "%I:%M%p (Asia/Tokyo)", gmtime(&session));
            strftime(week_reset, sizeof(week_reset), "%b %e at %I:%M%p (Asia/Tokyo)", gmtime(&week));
            printf("\r\033[119A\033[2KCurrent session\r\n\033[2K11%% used\r\n\033[2KResets %s\r\n\033[2KCurrent week (all models)\r\n\033[2K22%% used\r\n\033[2KResets %s\r\n\033[2KEsc to cancel\r\n", session_reset, week_reset); fflush(stdout);
            length = 0; memset(command, 0, sizeof(command));
        }
    }
}
