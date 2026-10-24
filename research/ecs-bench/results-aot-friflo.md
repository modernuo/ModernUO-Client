mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
NativeAOT.CreateSchema()
| Friflo | 56 | 44.4 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Friflo | 653.2 | 674.2 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Friflo | 156.9 | 308.9 | 38 | 0 | 1358110071693720 |
| MobileTick | Friflo | 6.8 | 8.3 | 0 | 0 | 4846990000 |
| PacketLookups | Friflo | 426.7 | 444.4 | 0 | 0 | 7454217650 |
| ChurnFrame | Friflo | 71.0 | 79.3 | 0 | 0 | 232792004 |
| ChurnManyQueries | Friflo | 74.4 | 95.6 | 0 | 0 | 2026468080 |
| BlockStream | Friflo | 147.8 | 154.6 | 0 | 0 | 269520560 |
| DirtyTag | Friflo | 73.8 | 83.0 | 0 | 0 | 199724050 |
| DirtyTicks | Friflo | n/a | | | | |
| Snapshot | Friflo | 3.8 | 4.4 | 0 | 0 | 4731030400 |
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
NativeAOT.CreateSchema()
| Friflo | 45 | 44.4 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Friflo | 647.7 | 671.4 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Friflo | 160.0 | 249.1 | 0 | 0 | 1358110071693720 |
| MobileTick | Friflo | 6.8 | 8.3 | 0 | 0 | 4846990000 |
| PacketLookups | Friflo | 441.0 | 494.4 | 0 | 0 | 7454217650 |
| ChurnFrame | Friflo | 75.0 | 87.9 | 0 | 0 | 232792004 |
| ChurnManyQueries | Friflo | 73.6 | 92.1 | 0 | 0 | 2026468080 |
| BlockStream | Friflo | 148.0 | 154.2 | 0 | 0 | 269520560 |
| DirtyTag | Friflo | 72.7 | 82.0 | 0 | 0 | 199724050 |
| DirtyTicks | Friflo | n/a | | | | |
| Snapshot | Friflo | 3.8 | 3.9 | 0 | 0 | 4731030400 |
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
NativeAOT.CreateSchema()
| Friflo | 47 | 44.4 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Friflo | 654.2 | 675.2 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Friflo | 157.1 | 176.5 | 0 | 0 | 1358110071693720 |
| MobileTick | Friflo | 6.7 | 8.2 | 0 | 0 | 4846990000 |
| PacketLookups | Friflo | 399.8 | 437.9 | 0 | 0 | 7454217650 |
| ChurnFrame | Friflo | 70.4 | 78.2 | 0 | 0 | 232792004 |
| ChurnManyQueries | Friflo | 73.6 | 81.9 | 0 | 0 | 2026468080 |
| BlockStream | Friflo | 148.5 | 160.6 | 0 | 0 | 269520560 |
| DirtyTag | Friflo | 79.3 | 113.2 | 0 | 0 | 199724050 |
| DirtyTicks | Friflo | n/a | | | | |
| Snapshot | Friflo | 3.8 | 3.9 | 0 | 0 | 4731030400 |
