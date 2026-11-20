# Third-party notices

This client contains code from the projects below. Each copied file keeps its original header and names its source file and
commit.

## ModernUO

Files adapted or copied from ModernUO (https://github.com/modernuo/ModernUO). ModernUO is GPL-3.0; these files are used
here outside its GPL under agreement from its author, Kamron Batman.

| File here | ModernUO source | Commit |
|---|---|---|
| `src/ClassicUO.IO/Buffers/SpanReader.cs` | `Projects/Server/Buffers/SpanReader.cs` (namespace, partial, client text rules via `SpanText`) | 040425163 |
| `src/ClassicUO.IO/Buffers/SpanWriter.cs` | `Projects/Server/Buffers/SpanWriter.cs` (namespace, partial, `ArrayPool<byte>.Shared`, no interpolated or `Serial` overloads) | 15e506ffc |

## IORingGroup (BSD-3-Clause)

The event-loop waiters in `src/ClassicUO.Client/Loop/` (`LoopWin32.cs`, `LoopLinux.cs`, `LoopMacOS.cs` and their waiters)
adapt code from IORingGroup: [licenses/IORingGroup-LICENSE](licenses/IORingGroup-LICENSE).
