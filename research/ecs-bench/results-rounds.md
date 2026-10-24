mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| TinyEcs | 168 | 71.6 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | TinyEcs | 846.2 | 897.9 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | TinyEcs | 349.3 | 425.0 | 11626 | 0 | 1358110071693720 |
| MobileTick | TinyEcs | 12.0 | 14.6 | 0 | 0 | 4846990000 |
| PacketLookups | TinyEcs | 654.5 | 1100.3 | 0 | 0 | 7454217650 |
| ChurnFrame | TinyEcs | 214.7 | 470.9 | 19 | 0 | 232792004 |
| ChurnManyQueries | TinyEcs | 115.2 | 150.1 | 19 | 0 | 2026468080 |
| BlockStream | TinyEcs | 154.0 | 159.8 | 0 | 0 | 269520560 |
| DirtyTag | TinyEcs | 93.7 | 108.9 | 13 | 0 | 199724050 |
| DirtyTicks | TinyEcs | 55.6 | 62.1 | 0 | 0 | 199724050 |
| Snapshot | TinyEcs | 6.7 | 7.0 | 0 | 0 | 4731030400 |
mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
Assemblies loaded: 69, duration: 8 ms, engine-dependants: [Friflo.Engine.ECS (9),  EcsBench (38)]
| Friflo | 240 | 44.4 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Friflo | 740.6 | 830.9 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Friflo | 235.8 | 465.3 | 0 | 0 | 1358110071693720 |
| MobileTick | Friflo | 11.1 | 13.7 | 0 | 0 | 4846990000 |
| PacketLookups | Friflo | 433.4 | 847.5 | 0 | 0 | 7454217650 |
| ChurnFrame | Friflo | 177.4 | 221.4 | 0 | 0 | 232792004 |
| ChurnManyQueries | Friflo | 175.9 | 232.4 | 0 | 0 | 2026468080 |
| BlockStream | Friflo | 90.4 | 98.9 | 0 | 0 | 269520560 |
| DirtyTag | Friflo | 80.9 | 94.9 | 0 | 0 | 199724050 |
| DirtyTicks | Friflo | n/a | | | | |
| Snapshot | Friflo | 6.7 | 8.3 | 0 | 0 | 4731030400 |
mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| Arch | 161 | 32.7 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Arch | 895.1 | 984.1 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Arch | 890.2 | 1045.9 | 0 | 0 | 1358110071693720 |
| MobileTick | Arch | 11.2 | 14.4 | 0 | 0 | 4846990000 |
| PacketLookups | Arch | 565.9 | 1782.5 | 0 | 0 | 7454217650 |
| ChurnFrame | Arch | 156.2 | 397.2 | 294 | 0 | 232792004 |
| ChurnManyQueries | Arch | 120.1 | 176.5 | 294 | 0 | 2026468080 |
| BlockStream | Arch | 141.0 | 161.1 | 0 | 0 | 269520560 |
| DirtyTag | Arch | 102.4 | 156.5 | 952 | 0 | 199724050 |
| DirtyTicks | Arch | n/a | | | | |
| Snapshot | Arch | 7.6 | 8.3 | 0 | 0 | 4731030400 |
mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
Assemblies loaded: 69, duration: 8 ms, engine-dependants: [Friflo.Engine.ECS (9),  EcsBench (38)]
| Friflo | 253 | 44.4 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Friflo | 745.6 | 810.1 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Friflo | 238.0 | 370.4 | 0 | 0 | 1358110071693720 |
| MobileTick | Friflo | 11.2 | 14.0 | 0 | 0 | 4846990000 |
| PacketLookups | Friflo | 437.2 | 893.9 | 0 | 0 | 7454217650 |
| ChurnFrame | Friflo | 207.4 | 301.5 | 0 | 0 | 232792004 |
| ChurnManyQueries | Friflo | 67.0 | 92.9 | 0 | 0 | 2026468080 |
| BlockStream | Friflo | 93.5 | 114.6 | 0 | 0 | 269520560 |
| DirtyTag | Friflo | 83.7 | 156.8 | 0 | 0 | 199724050 |
| DirtyTicks | Friflo | n/a | | | | |
| Snapshot | Friflo | 6.7 | 7.1 | 0 | 0 | 4731030400 |
mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| Arch | 163 | 32.7 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Arch | 908.1 | 1158.1 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Arch | 1249.7 | 1396.3 | 0 | 0 | 1358110071693720 |
| MobileTick | Arch | 11.2 | 13.5 | 0 | 0 | 4846990000 |
| PacketLookups | Arch | 507.7 | 1046.9 | 0 | 0 | 7454217650 |
| ChurnFrame | Arch | 157.5 | 308.1 | 294 | 0 | 232792004 |
| ChurnManyQueries | Arch | 120.5 | 145.0 | 294 | 0 | 2026468080 |
| BlockStream | Arch | 138.3 | 143.0 | 0 | 0 | 269520560 |
| DirtyTag | Arch | 77.1 | 99.3 | 948 | 0 | 199724050 |
| DirtyTicks | Arch | n/a | | | | |
| Snapshot | Arch | 7.7 | 8.0 | 0 | 0 | 4731030400 |
mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| TinyEcs | 168 | 71.6 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | TinyEcs | 819.3 | 861.0 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | TinyEcs | 310.6 | 362.9 | 11572 | 0 | 1358110071693720 |
| MobileTick | TinyEcs | 11.7 | 14.1 | 0 | 0 | 4846990000 |
| PacketLookups | TinyEcs | 683.4 | 1160.0 | 0 | 0 | 7454217650 |
| ChurnFrame | TinyEcs | 174.8 | 539.8 | 19 | 0 | 232792004 |
| ChurnManyQueries | TinyEcs | 117.8 | 152.8 | 19 | 0 | 2026468080 |
| BlockStream | TinyEcs | 155.9 | 162.9 | 0 | 0 | 269520560 |
| DirtyTag | TinyEcs | 105.0 | 142.7 | 13 | 0 | 199724050 |
| DirtyTicks | TinyEcs | 57.2 | 63.8 | 0 | 0 | 199724050 |
| Snapshot | TinyEcs | 6.6 | 6.9 | 0 | 0 | 4731030400 |
mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| Arch | 153 | 32.7 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Arch | 831.6 | 990.1 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Arch | 1235.2 | 1409.7 | 0 | 0 | 1358110071693720 |
| MobileTick | Arch | 11.8 | 14.4 | 0 | 0 | 4846990000 |
| PacketLookups | Arch | 503.1 | 1031.3 | 0 | 0 | 7454217650 |
| ChurnFrame | Arch | 214.5 | 375.9 | 294 | 0 | 232792004 |
| ChurnManyQueries | Arch | 112.1 | 162.7 | 294 | 0 | 2026468080 |
| BlockStream | Arch | 142.1 | 148.9 | 0 | 0 | 269520560 |
| DirtyTag | Arch | 72.5 | 82.2 | 952 | 0 | 199724050 |
| DirtyTicks | Arch | n/a | | | | |
| Snapshot | Arch | 7.8 | 8.2 | 0 | 0 | 4731030400 |
mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| TinyEcs | 163 | 71.6 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | TinyEcs | 805.0 | 851.9 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | TinyEcs | 332.0 | 352.7 | 11565 | 0 | 1358110071693720 |
| MobileTick | TinyEcs | 12.1 | 15.0 | 0 | 0 | 4846990000 |
| PacketLookups | TinyEcs | 684.0 | 1322.8 | 0 | 0 | 7454217650 |
| ChurnFrame | TinyEcs | 161.2 | 569.7 | 19 | 0 | 232792004 |
| ChurnManyQueries | TinyEcs | 159.7 | 220.1 | 19 | 0 | 2026468080 |
| BlockStream | TinyEcs | 157.1 | 166.1 | 0 | 0 | 269520560 |
| DirtyTag | TinyEcs | 107.8 | 159.7 | 13 | 0 | 199724050 |
| DirtyTicks | TinyEcs | 58.1 | 73.2 | 8 | 0 | 199724050 |
| Snapshot | TinyEcs | 6.6 | 6.9 | 0 | 0 | 4731030400 |
mode=JIT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
Assemblies loaded: 69, duration: 8 ms, engine-dependants: [Friflo.Engine.ECS (9),  EcsBench (38)]
| Friflo | 249 | 44.4 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Friflo | 736.2 | 784.4 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Friflo | 237.3 | 400.0 | 0 | 0 | 1358110071693720 |
| MobileTick | Friflo | 11.2 | 13.7 | 0 | 0 | 4846990000 |
| PacketLookups | Friflo | 452.2 | 859.3 | 0 | 0 | 7454217650 |
| ChurnFrame | Friflo | 157.3 | 198.3 | 0 | 0 | 232792004 |
| ChurnManyQueries | Friflo | 77.5 | 209.6 | 0 | 0 | 2026468080 |
| BlockStream | Friflo | 89.2 | 93.9 | 0 | 0 | 269520560 |
| DirtyTag | Friflo | 75.2 | 84.5 | 0 | 0 | 199724050 |
| DirtyTicks | Friflo | n/a | | | | |
| Snapshot | Friflo | 6.7 | 7.1 | 0 | 0 | 4731030400 |
