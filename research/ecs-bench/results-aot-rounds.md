mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| TinyEcs | 57 | 71.6 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | TinyEcs | 1326.2 | 1404.7 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | TinyEcs | 300.5 | 411.3 | 11539 | 0 | 1358110071693720 |
| MobileTick | TinyEcs | 7.6 | 9.2 | 0 | 0 | 4846990000 |
| PacketLookups | TinyEcs | 806.5 | 852.3 | 0 | 0 | 7454217650 |
| ChurnFrame | TinyEcs | 111.6 | 129.8 | 19 | 0 | 232792004 |
| ChurnManyQueries | TinyEcs | 132.3 | 165.2 | 19 | 0 | 2026468080 |
| BlockStream | TinyEcs | 208.2 | 229.6 | 0 | 0 | 269520560 |
| DirtyTag | TinyEcs | 109.5 | 141.2 | 13 | 0 | 199724050 |
| DirtyTicks | TinyEcs | 49.4 | 52.5 | 0 | 0 | 199724050 |
| Snapshot | TinyEcs | 4.3 | 4.7 | 0 | 0 | 4731030400 |
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
EntitySchema not created.
NativeAOT requires schema creation on startup:
1. Create NativeAOT instance:   var aot = new NativeAOT();
2. Register types with:         aot.Register...(); 
3. Finish with:                 aot.CreateSchema();
Using default EntitySchema
Unhandled exception. System.TypeInitializationException: A type initializer threw an exception. To determine which type, inspect the InnerException's StackTrace property.
 ---> System.InvalidOperationException: ITag type not found: EcsBench.Land
   at Friflo.Engine.ECS.SchemaTypeUtils.GetTagIndex(Type) + 0x6e
   at Friflo.Engine.ECS.TagInfo`1..cctor() + 0x1f
   at System.Runtime.CompilerServices.ClassConstructorRunner.EnsureClassConstructorRun(StaticClassConstructionContext*) + 0xb4
   --- End of inner exception stack trace ---
   at System.Runtime.CompilerServices.ClassConstructorRunner.EnsureClassConstructorRun(StaticClassConstructionContext*) + 0x13a
   at System.Runtime.CompilerServices.ClassConstructorRunner.CheckStaticClassConstructionReturnNonGCStaticBase(StaticClassConstructionContext*, IntPtr) + 0xd
   at Friflo.Engine.ECS.Tags.Get[T]() + 0x57
   at EcsBench.FrifloAdapter.Populate() + 0x78
   at Program.<Main>$(String[] args) + 0x9ae
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| Arch | 43 | 32.8 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Arch | 733.7 | 817.0 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Arch | 360.1 | 414.6 | 0 | 0 | 1358110071693720 |
| MobileTick | Arch | 7.3 | 9.1 | 0 | 0 | 4846990000 |
| PacketLookups | Arch | 502.6 | 563.9 | 0 | 0 | 7454217650 |
| ChurnFrame | Arch | 101.5 | 113.8 | 294 | 0 | 232792004 |
| ChurnManyQueries | Arch | 108.7 | 124.5 | 294 | 0 | 2026468080 |
| BlockStream | Arch | 180.6 | 187.3 | 0 | 0 | 269520560 |
| DirtyTag | Arch | 72.4 | 77.7 | 952 | 0 | 199724050 |
| DirtyTicks | Arch | n/a | | | | |
| Snapshot | Arch | 4.1 | 4.2 | 0 | 0 | 4731030400 |
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
EntitySchema not created.
NativeAOT requires schema creation on startup:
1. Create NativeAOT instance:   var aot = new NativeAOT();
2. Register types with:         aot.Register...(); 
3. Finish with:                 aot.CreateSchema();
Using default EntitySchema
Unhandled exception. System.TypeInitializationException: A type initializer threw an exception. To determine which type, inspect the InnerException's StackTrace property.
 ---> System.InvalidOperationException: ITag type not found: EcsBench.Land
   at Friflo.Engine.ECS.SchemaTypeUtils.GetTagIndex(Type) + 0x6e
   at Friflo.Engine.ECS.TagInfo`1..cctor() + 0x1f
   at System.Runtime.CompilerServices.ClassConstructorRunner.EnsureClassConstructorRun(StaticClassConstructionContext*) + 0xb4
   --- End of inner exception stack trace ---
   at System.Runtime.CompilerServices.ClassConstructorRunner.EnsureClassConstructorRun(StaticClassConstructionContext*) + 0x13a
   at System.Runtime.CompilerServices.ClassConstructorRunner.CheckStaticClassConstructionReturnNonGCStaticBase(StaticClassConstructionContext*, IntPtr) + 0xd
   at Friflo.Engine.ECS.Tags.Get[T]() + 0x57
   at EcsBench.FrifloAdapter.Populate() + 0x78
   at Program.<Main>$(String[] args) + 0x9ae
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| Arch | 41 | 32.8 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Arch | 719.3 | 776.9 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Arch | 359.3 | 458.4 | 0 | 0 | 1358110071693720 |
| MobileTick | Arch | 7.1 | 8.7 | 0 | 0 | 4846990000 |
| PacketLookups | Arch | 522.8 | 584.1 | 0 | 0 | 7454217650 |
| ChurnFrame | Arch | 106.7 | 134.5 | 294 | 0 | 232792004 |
| ChurnManyQueries | Arch | 110.0 | 120.5 | 294 | 0 | 2026468080 |
| BlockStream | Arch | 182.9 | 197.0 | 0 | 0 | 269520560 |
| DirtyTag | Arch | 75.0 | 86.4 | 952 | 0 | 199724050 |
| DirtyTicks | Arch | n/a | | | | |
| Snapshot | Arch | 4.1 | 4.2 | 0 | 0 | 4731030400 |
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| TinyEcs | 55 | 71.6 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | TinyEcs | 1301.5 | 1430.1 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | TinyEcs | 315.7 | 392.9 | 11538 | 0 | 1358110071693720 |
| MobileTick | TinyEcs | 7.6 | 9.3 | 0 | 0 | 4846990000 |
| PacketLookups | TinyEcs | 813.8 | 877.4 | 0 | 0 | 7454217650 |
| ChurnFrame | TinyEcs | 112.3 | 153.7 | 19 | 0 | 232792004 |
| ChurnManyQueries | TinyEcs | 131.2 | 159.3 | 19 | 0 | 2026468080 |
| BlockStream | TinyEcs | 208.6 | 219.8 | 0 | 0 | 269520560 |
| DirtyTag | TinyEcs | 103.3 | 120.6 | 13 | 0 | 199724050 |
| DirtyTicks | TinyEcs | 48.4 | 52.7 | 0 | 0 | 199724050 |
| Snapshot | TinyEcs | 4.1 | 4.3 | 0 | 0 | 4731030400 |
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| Arch | 45 | 32.8 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | Arch | 711.7 | 822.9 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | Arch | 363.3 | 460.0 | 38 | 0 | 1358110071693720 |
| MobileTick | Arch | 7.1 | 8.7 | 0 | 0 | 4846990000 |
| PacketLookups | Arch | 518.8 | 613.0 | 0 | 0 | 7454217650 |
| ChurnFrame | Arch | 103.7 | 117.9 | 294 | 0 | 232792004 |
| ChurnManyQueries | Arch | 110.9 | 135.1 | 294 | 0 | 2026468080 |
| BlockStream | Arch | 184.7 | 191.9 | 0 | 0 | 269520560 |
| DirtyTag | Arch | 72.7 | 79.0 | 952 | 0 | 199724050 |
| DirtyTicks | Arch | n/a | | | | |
| Snapshot | Arch | 4.2 | 4.9 | 0 | 0 | 4731030400 |
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
| TinyEcs | 55 | 71.6 |

| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |
|---|---|---:|---:|---:|---:|---|
| RenderExtract | TinyEcs | 1344.7 | 1456.7 | 0 | 0 | 1358110071693720 |
| RenderExtractParallel | TinyEcs | 272.5 | 311.4 | 11576 | 0 | 1358110071693720 |
| MobileTick | TinyEcs | 7.5 | 9.1 | 0 | 0 | 4846990000 |
| PacketLookups | TinyEcs | 810.4 | 949.1 | 0 | 0 | 7454217650 |
| ChurnFrame | TinyEcs | 114.1 | 157.5 | 19 | 0 | 232792004 |
| ChurnManyQueries | TinyEcs | 131.7 | 179.4 | 19 | 0 | 2026468080 |
| BlockStream | TinyEcs | 204.5 | 235.7 | 0 | 0 | 269520560 |
| DirtyTag | TinyEcs | 105.7 | 128.7 | 13 | 0 | 199724050 |
| DirtyTicks | TinyEcs | 50.9 | 63.3 | 0 | 0 | 199724050 |
| Snapshot | TinyEcs | 4.3 | 4.8 | 0 | 0 | 4731030400 |
mode=AOT cores=24 runtime=10.0.13

| Library | Populate (555k entities) ms | Retained heap MB |
|---|---:|---:|
EntitySchema not created.
NativeAOT requires schema creation on startup:
1. Create NativeAOT instance:   var aot = new NativeAOT();
2. Register types with:         aot.Register...(); 
3. Finish with:                 aot.CreateSchema();
Using default EntitySchema
Unhandled exception. System.TypeInitializationException: A type initializer threw an exception. To determine which type, inspect the InnerException's StackTrace property.
 ---> System.InvalidOperationException: ITag type not found: EcsBench.Land
   at Friflo.Engine.ECS.SchemaTypeUtils.GetTagIndex(Type) + 0x6e
   at Friflo.Engine.ECS.TagInfo`1..cctor() + 0x1f
   at System.Runtime.CompilerServices.ClassConstructorRunner.EnsureClassConstructorRun(StaticClassConstructionContext*) + 0xb4
   --- End of inner exception stack trace ---
   at System.Runtime.CompilerServices.ClassConstructorRunner.EnsureClassConstructorRun(StaticClassConstructionContext*) + 0x13a
   at System.Runtime.CompilerServices.ClassConstructorRunner.CheckStaticClassConstructionReturnNonGCStaticBase(StaticClassConstructionContext*, IntPtr) + 0xd
   at Friflo.Engine.ECS.Tags.Get[T]() + 0x57
   at EcsBench.FrifloAdapter.Populate() + 0x78
   at Program.<Main>$(String[] args) + 0x9ae
