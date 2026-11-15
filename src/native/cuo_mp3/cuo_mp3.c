/* SPDX-License-Identifier: BSD-2-Clause */
/* See cuo_mp3.h. Compiled with DR_MP3_NO_STDIO: the client decodes only from memory, so no fopen/_wfopen paths. */

#define DR_MP3_IMPLEMENTATION
#define DR_MP3_NO_STDIO
#include "../../../external/dr_libs/dr_mp3.h"

#include "cuo_mp3.h"

struct cuo_mp3
{
    drmp3 mp3;
};

CUO_MP3_API uint32_t cuo_mp3_version(void)
{
    return ((uint32_t)DRMP3_VERSION_MAJOR << 16) | ((uint32_t)DRMP3_VERSION_MINOR << 8) | (uint32_t)DRMP3_VERSION_REVISION;
}

CUO_MP3_API cuo_mp3* cuo_mp3_open_memory(const void* data, size_t size)
{
    cuo_mp3* h;

    if (data == NULL || size == 0)
    {
        return NULL;
    }

    h = (cuo_mp3*)drmp3_malloc(sizeof(cuo_mp3), NULL);

    if (h == NULL)
    {
        return NULL;
    }

    if (!drmp3_init_memory(&h->mp3, data, size, NULL))
    {
        drmp3_free(h, NULL);
        return NULL;
    }

    /* dr_mp3 accepts any frame rate/channel count the header allows; the client only plays 1-2 channels. */
    if (h->mp3.channels < 1 || h->mp3.channels > 2 || h->mp3.sampleRate < 8000 || h->mp3.sampleRate > 48000)
    {
        drmp3_uninit(&h->mp3);
        drmp3_free(h, NULL);
        return NULL;
    }

    return h;
}

CUO_MP3_API void cuo_mp3_close(cuo_mp3* mp3)
{
    if (mp3 != NULL)
    {
        drmp3_uninit(&mp3->mp3);
        drmp3_free(mp3, NULL);
    }
}

CUO_MP3_API int32_t cuo_mp3_get_info(const cuo_mp3* mp3, cuo_mp3_info* info)
{
    if (mp3 == NULL || info == NULL)
    {
        return 0;
    }

    info->sample_rate = mp3->mp3.sampleRate;
    info->channels = mp3->mp3.channels;
    info->delay_frames = mp3->mp3.delayInPCMFrames;
    info->padding_frames = mp3->mp3.paddingInPCMFrames;
    info->raw_frames = mp3->mp3.totalPCMFrameCount;

    return 1;
}

CUO_MP3_API uint64_t cuo_mp3_read_s16(cuo_mp3* mp3, uint64_t frames, int16_t* out)
{
    if (mp3 == NULL || out == NULL || frames == 0)
    {
        return 0;
    }

    return drmp3_read_pcm_frames_s16(&mp3->mp3, frames, out);
}

CUO_MP3_API int32_t cuo_mp3_seek(cuo_mp3* mp3, uint64_t frame)
{
    if (mp3 == NULL)
    {
        return 0;
    }

    return drmp3_seek_to_pcm_frame(&mp3->mp3, frame) ? 1 : 0;
}

CUO_MP3_API uint64_t cuo_mp3_get_frame_count(cuo_mp3* mp3)
{
    drmp3_uint64 raw, trim;

    if (mp3 == NULL)
    {
        return 0;
    }

    raw = mp3->mp3.totalPCMFrameCount;

    if (raw == DRMP3_UINT64_MAX)
    {
        /* No Xing tag: delay and padding are 0, so the scan's count is the audible count. */
        return drmp3_get_pcm_frame_count(&mp3->mp3);
    }

    trim = (drmp3_uint64)mp3->mp3.delayInPCMFrames + mp3->mp3.paddingInPCMFrames;

    return raw > trim ? raw - trim : 0;
}
