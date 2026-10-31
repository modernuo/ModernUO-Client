# SPDX-License-Identifier: BSD-2-Clause
"""Writes Fixtures/seam-noise.mp3: a small, redistributable, non-silent synthetic MP3 for Mp3DecoderTests.

No encoder and no recorded audio: an Info + LAME frame (delay 576, padding 700, frame count) followed by
MPEG-1 Layer III frames (128 kbps, 44.1 kHz, joint stereo, 417 bytes). Each frame is self-contained
(main_data_begin 0) with sane side info (big_values 120, Huffman table 15 in every region, global_gain 165,
no scale factors) and pseudo-random main data from a fixed seed, so it decodes to deterministic noise that
differs from frame to frame. The loop-seam test needs exactly that: a wrap at the wrong sample is visible.
Usage: python -I make_mp3_fixture.py [out-path]   (default: seam-noise.mp3 next to this script)
"""
import os
import random
import sys

FRAMES = 24
FRAME_BYTES = 417
HEADER = bytes([0xFF, 0xFB, 0x90, 0x44])


class Bits:
    def __init__(self):
        self.bits = []

    def put(self, value, n):
        for i in range(n - 1, -1, -1):
            self.bits.append((value >> i) & 1)

    def bytes(self):
        assert len(self.bits) % 8 == 0
        return bytes(int("".join(map(str, self.bits[i:i + 8])), 2) for i in range(0, len(self.bits), 8))


def side_info(part23):
    b = Bits()
    b.put(0, 9)   # main_data_begin: no bit reservoir, every frame stands alone
    b.put(0, 3)   # private bits
    b.put(0, 8)   # scfsi, 2 channels x 4 bands
    for _gr in range(2):
        for _ch in range(2):
            b.put(part23, 12)  # part2_3_length
            b.put(120, 9)      # big_values (pairs)
            b.put(165, 8)      # global_gain
            b.put(0, 4)        # scalefac_compress: no scale factor bits
            b.put(0, 1)        # window_switching_flag: long blocks
            for _ in range(3):
                b.put(15, 5)   # table_select
            b.put(7, 4)        # region0_count
            b.put(3, 3)        # region1_count
            b.put(0, 1)        # preflag
            b.put(0, 1)        # scalefac_scale
            b.put(0, 1)        # count1table_select
    out = b.bytes()
    assert len(out) == 32
    return out


def info_frame(frames, delay, padding):
    f = bytearray(FRAME_BYTES)
    f[0:4] = HEADER
    at = 4 + 32
    f[at:at + 4] = b"Info"
    f[at + 7] = 0x01  # FRAMES flag
    f[at + 8:at + 12] = frames.to_bytes(4, "big")
    lame = at + 12
    f[lame:lame + 9] = b"LAME3.100"
    f[lame + 21] = delay >> 4
    f[lame + 22] = ((delay & 0xF) << 4) | ((padding >> 8) & 0xF)
    f[lame + 23] = padding & 0xFF
    return bytes(f)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "seam-noise.mp3")
    rng = random.Random(0x5EA4)
    main_bytes = FRAME_BYTES - 4 - 32
    part23 = main_bytes * 8 // 4
    data = bytearray(info_frame(FRAMES, 576, 700))
    for _ in range(FRAMES):
        data += HEADER + side_info(part23) + bytes(rng.getrandbits(8) for _ in range(main_bytes))
    with open(out, "wb") as fh:
        fh.write(data)
    print(f"{out}: {len(data)} bytes, {FRAMES} audio frames")


if __name__ == "__main__":
    main()
