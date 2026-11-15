#!/bin/bash
# Seeds for the fuzzer from real tracks: the first 16 KB (ID3, Xing/Info/LAME, first frames), 16 KB from the middle
# and the last 8 KB (ID3v1/APE tags, the final frames) of every MP3 in a folder. The seeds are UO data: keep them out
# of the repository.
# Usage: bash make-seeds.sh "<UO>/Music/Digital" <seed-dir>
set -euo pipefail
src="$1"; out="$2"; mkdir -p "$out"
for f in "$src"/*.mp3; do
  b="$(basename "$f" .mp3)"; size=$(stat -c %s "$f")
  head -c 16384 "$f" > "$out/$b.head"
  dd if="$f" of="$out/$b.mid" bs=1 skip=$((size / 2)) count=16384 status=none
  tail -c 8192 "$f" > "$out/$b.tail"
done
ls "$out" | wc -l
