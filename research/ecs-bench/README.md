# ECS benchmark harness

UO-shaped benchmark of TinyEcs, Friflo.Engine.ECS and Arch. The write-up is in [docs/modern-client/ecs-benchmarks.md](../../docs/modern-client/ecs-benchmarks.md).

## Setup

Clone these next to this repo, so they sit beside `classicuo`:

```
git clone --recurse-submodules https://github.com/andreakarasho/TinyEcs.git TinyECS
git clone https://github.com/friflo/Friflo.Engine.ECS.git
git clone https://github.com/genaray/Arch.git
```

## Run (JIT)

```
dotnet build research/ecs-bench/EcsBench.csproj -c Release
dotnet research/ecs-bench/bin/Release/net10.0/EcsBench.dll [benchmark|*] [TinyEcs|Friflo|Arch|*]
```

- Run one library per process to keep thread pools from interfering.
- `BENCH_THREADS` sets the worker count for the parallel benchmark (default: logical cores / 2).
- The process forces `Environment.Exit(0)`, because Arch's job scheduler and Friflo's job runner leave foreground threads alive.

## Run (NativeAOT, Windows)

Requires the MSVC x64 build tools and the Windows 11 SDK. From a VS developer environment, with `vswhere.exe` on PATH:

```
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"
dotnet publish research/ecs-bench/EcsBench.csproj -c Release -r win-x64 -p:AotBuild=true -p:IlcUseEnvironmentalTools=true -o aot
aot\EcsBench.exe
```

`AotBuild` scopes `PublishAot` to this project. Passing `-p:PublishAot=true` globally fails, because it also applies to the libraries' net6.0/netstandard targets.

## Files

- `Shared.cs`: components, world spec, adapter base class.
- `*Adapter.cs`: one per library, each using its fastest native API.
- `results-rounds.md`: JIT, 3 rotated rounds. `results-aot-rounds.md` / `results-aot-friflo.md`: NativeAOT.
- `aot-warnings.txt`: trim and AOT analysis warnings.
