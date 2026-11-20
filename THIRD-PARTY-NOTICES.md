# Third-party notices

This client contains code from the projects below. Each copied file keeps its original header and names its source file and
commit.

## ModernUO

Files adapted or copied from ModernUO (https://github.com/modernuo/ModernUO). ModernUO is GPL-3.0; these files are used
here outside its GPL under agreement from its author, Kamron Batman.

| File here | ModernUO source | Commit |
|---|---|---|
| `src/ClassicUO.Utility/Cp1252.cs` | `Projects/Server/Text/TextEncoding.cs` (GetStringLatin1's single-allocation decode and safe-mode pre-scan, adapted to Windows-1252 and the client's safe rule; span encoders) | 8e2ddc7ab |

## IORingGroup (BSD-3-Clause)

The event-loop waiters in `src/ClassicUO.Client/Loop/` (`LoopWin32.cs`, `LoopLinux.cs`, `LoopMacOS.cs` and their waiters)
adapt code from IORingGroup: [licenses/IORingGroup-LICENSE](licenses/IORingGroup-LICENSE).
