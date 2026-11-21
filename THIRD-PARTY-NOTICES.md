# Third-party notices

This client contains code from the projects below. Each copied file keeps its original header and names its source file and
commit.

## ModernUO

Files adapted or copied from ModernUO (https://github.com/modernuo/ModernUO). ModernUO is GPL-3.0; these files are used
here outside its GPL under agreement from its author, Kamron Batman.

| File here | ModernUO source | Commit |
|---|---|---|
| `src/Uo.Buffers/STArrayPool.cs` | `Projects/Server/Buffers/STArrayPool.cs` (adapted: one pool per thread) | 3161a5ba4 |
| `src/Uo.Buffers/PooledRefList.cs` | `Projects/Server/Collections/PooledRefList.cs` | b19149856 |
| `src/Uo.Buffers/PooledRefQueue.cs` | `Projects/Server/Collections/PooledRefQueue.cs` | ebaf10493 |
| `src/Uo.Buffers/ValueStringBuilder.cs` | `Projects/Server/Buffers/ValueStringBuilder.cs` | 61e41df00 |
| `src/Uo.Buffers/CollectionThrowStrings.cs` | `Projects/Server/Collections/CollectionThrowStrings.cs` | e1e1a7c64 |
| `src/ClassicUO.Utility/Cp1252.cs` | `Projects/Server/Text/TextEncoding.cs` (GetStringLatin1's single-allocation decode and safe-mode pre-scan, adapted to Windows-1252 and the client's safe rule; span encoders) | 8e2ddc7ab |

## .NET runtime (MIT)

`STArrayPool.cs` adapts .NET's `TlsOverPerCoreLockedStacksArrayPool`; `PooledRefList.cs`, `PooledRefQueue.cs` and
`ValueStringBuilder.cs` derive from .NET's `List<T>`, `Queue<T>` and `ValueStringBuilder`. Copyright (c) .NET Foundation and
Contributors, MIT License: [licenses/dotnet-MIT-LICENSE](licenses/dotnet-MIT-LICENSE).

## IORingGroup (BSD-3-Clause)

The event-loop waiters in `src/ClassicUO.Client/Loop/` (`LoopWin32.cs`, `LoopLinux.cs`, `LoopMacOS.cs` and their waiters)
adapt code from IORingGroup: [licenses/IORingGroup-LICENSE](licenses/IORingGroup-LICENSE).
