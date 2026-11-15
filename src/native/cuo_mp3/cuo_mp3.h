/* SPDX-License-Identifier: BSD-2-Clause */
/*
 * cuo_mp3: a tiny flat C API over dr_mp3 (external/dr_libs/dr_mp3.h, v0.7.4) for the client's P/Invoke.
 *
 * The decoder handle is opaque, so managed code never depends on the layout of drmp3. The caller owns the MP3 bytes
 * and must keep them alive (and unmoved) until cuo_mp3_close. Reads return interleaved signed 16-bit PCM.
 *
 * Gapless: dr_mp3 skips the Xing/Info frame and the LAME encoder delay, and trims the LAME padding, inside its reads
 * (drmp3_read_pcm_frames_raw). Callers must not trim again; cuo_mp3_info reports the values for logging.
 */
#ifndef CUO_MP3_H
#define CUO_MP3_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32) && defined(CUO_MP3_SHARED)
#define CUO_MP3_API __declspec(dllexport)
#elif defined(__GNUC__) && defined(CUO_MP3_SHARED)
#define CUO_MP3_API __attribute__((visibility("default")))
#else
#define CUO_MP3_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct cuo_mp3 cuo_mp3;

/* Mirrored by ClassicUO.IO.Audio.Mp3Native.Info: keep the field order and sizes in step. */
typedef struct cuo_mp3_info
{
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t delay_frames;     /* LAME encoder delay + 529 decoder delay, skipped by dr_mp3; 0 without a LAME tag */
    uint32_t padding_frames;   /* LAME padding - 529, trimmed by dr_mp3; 0 without a LAME tag */
    uint64_t raw_frames;       /* Xing frame count * samples per frame (includes delay and padding); UINT64_MAX if unknown */
} cuo_mp3_info;

/* (major << 16) | (minor << 8) | revision of the dr_mp3 compiled in. */
CUO_MP3_API uint32_t cuo_mp3_version(void);

/* Opens a decoder over size bytes at data. Returns NULL if dr_mp3 finds no valid stream or allocation fails. */
CUO_MP3_API cuo_mp3* cuo_mp3_open_memory(const void* data, size_t size);

CUO_MP3_API void cuo_mp3_close(cuo_mp3* mp3);

/* Returns 1 and fills info, or 0 for a NULL argument. */
CUO_MP3_API int32_t cuo_mp3_get_info(const cuo_mp3* mp3, cuo_mp3_info* info);

/* Decodes up to frames PCM frames (frames * channels int16 samples) into out. Returns the frames written; fewer than
   asked means the end of the stream (or undecodable data). */
CUO_MP3_API uint64_t cuo_mp3_read_s16(cuo_mp3* mp3, uint64_t frames, int16_t* out);

/* Seeks to a PCM frame as the caller counts them (0 = the first audible frame after the delay). Returns 1 on success. */
CUO_MP3_API int32_t cuo_mp3_seek(cuo_mp3* mp3, uint64_t frame);

/* Audible PCM frames in the stream: from the Xing tag when present (minus delay and padding), else by scanning the
   frame headers (linear time, keeps the read position). Returns 0 on error. */
CUO_MP3_API uint64_t cuo_mp3_get_frame_count(cuo_mp3* mp3);

#ifdef __cplusplus
}
#endif

#endif
