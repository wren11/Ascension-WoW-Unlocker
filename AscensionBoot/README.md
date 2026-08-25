# AscensionBoot

Standalone 32-bit injector used by GMToolBox.

```
AscensionBoot.exe <stock-Ascension.exe> <runtime-dir> [client-args...]
```

It copies the stock exe into `<runtime-dir>\Ascension.launch.exe`, rewrites the
`Extensions.dll` import to `ExtProxy64.dll`, stages the proxy DLL into the
runtime folder, then starts the launch exe. It never writes into the live
Ascension install.

`ExtProxy64.dll` is resolved from:

1. the folder that contains `AscensionBoot.exe`
2. `../ExtProxy/ExtProxy64.dll` (this repo layout)

## Build

Requires i686 llvm-mingw `clang` on PATH (same toolchain as ExtProxy).

```powershell
cd AscensionBoot
.\build.ps1
```

That writes `AscensionBoot.exe` here and copies it next to `ExtProxy/` so GMT
can launch without extra path setup.

CMake is optional:

```powershell
cmake -S . -B build -G Ninja
cmake --build build
```
