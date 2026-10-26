/* Compacts or trims a clip with the 3DS app's flb.c, for the Windows app test
   to compare its own compacting and trimming with (they must write the same
   bytes).

   usage: compact_ref compact <clip>
          compact_ref trim <clip> <new clip> <first frame> <frame count>
   Exits with 1 (saying why) if it fails. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "flb.h"

int main(int argc, char **argv)
{
    FlbCompactor c;
    const char *error = NULL;
    bool begun;
    if (argc == 3 && strcmp(argv[1], "compact") == 0)
        begun = flbCompactBegin(&c, argv[2], &error);
    else if (argc == 6 && strcmp(argv[1], "trim") == 0)
        begun = flbTrimBegin(&c, argv[2], argv[3], (uint32_t)strtoul(argv[4], NULL, 10),
                             (uint32_t)strtoul(argv[5], NULL, 10), &error);
    else
        return 2;
    int r = -1;
    if (begun)
        while ((r = flbCompactStep(&c, &error)) == 0)
            ;
    if (r != 1)
    {
        printf("%s\n", error ? error : "failed");
        return 1;
    }
    return 0;
}
