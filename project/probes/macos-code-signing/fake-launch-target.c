#include <stdio.h>

#ifndef PROBE_MARKER_VALUE
#error PROBE_MARKER_VALUE must be set at compile time
#endif

int main(int argc, char **argv)
{
    if (argc != 2) return 64;
    FILE *marker = fopen(argv[1], "w");
    if (marker == NULL) return 65;
    if (fputs(PROBE_MARKER_VALUE, marker) < 0) {
        (void)fclose(marker);
        return 66;
    }
    return fclose(marker) == 0 ? 0 : 67;
}
