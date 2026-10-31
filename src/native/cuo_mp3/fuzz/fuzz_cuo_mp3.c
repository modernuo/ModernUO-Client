/* SPDX-License-Identifier: BSD-2-Clause */
/*
 * libFuzzer target for cuo_mp3 / dr_mp3: open from memory, read everything, seek (start, middle, past the end), read
 * again, count frames, close. Built by build-fuzz-win-x64.sh with MSVC -fsanitize=fuzzer,address.
 * The input is copied into an exact-size heap block so ASan sees any read past its end.
 */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "../cuo_mp3.h"

#define MAX_READ_FRAMES (1u << 20) /* about 24 s at 44.1 kHz: bounds the time per input */

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    static int16_t pcm[4096 * 2];
    uint8_t* copy;
    cuo_mp3* mp3;
    cuo_mp3_info info;
    uint64_t total = 0, n, count;

    if (size == 0)
    {
        return 0;
    }

    copy = (uint8_t*)malloc(size);
    if (copy == NULL)
    {
        return 0;
    }
    memcpy(copy, data, size);

    mp3 = cuo_mp3_open_memory(copy, size);

    if (mp3 != NULL)
    {
        cuo_mp3_get_info(mp3, &info);

        if (info.channels < 1 || info.channels > 2)
        {
            abort(); /* the wrapper promised 1-2 channels */
        }

        while (total < MAX_READ_FRAMES && (n = cuo_mp3_read_s16(mp3, 4096, pcm)) > 0)
        {
            if (n > 4096)
            {
                abort();
            }
            total += n;
        }

        if (cuo_mp3_seek(mp3, 0))
        {
            cuo_mp3_read_s16(mp3, 1000, pcm);
        }

        cuo_mp3_seek(mp3, total / 2);
        cuo_mp3_read_s16(mp3, 4096, pcm);
        cuo_mp3_seek(mp3, total + 100000);
        cuo_mp3_read_s16(mp3, 4096, pcm);

        if (total < MAX_READ_FRAMES)
        {
            count = cuo_mp3_get_frame_count(mp3);
            (void)count;
        }

        cuo_mp3_close(mp3);
    }

    free(copy);

    return 0;
}
