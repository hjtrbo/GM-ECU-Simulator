# Vendored runtime libraries

These DLLs are pre-built dependencies that the GmEcuSimulator project links against. They live here so the repo builds without needing the sibling Gmlan Data Logger source on disk.

| File | Source | Purpose |
| --- | --- | --- |
| `BinaryWorker.dll` | Sibling Gmlan Data Logger project (`BinaryWorker.csproj`) | Reads `.bin` log captures; backs `GmEcuSimulator/Replay/LogReaderBinSource.cs`. |
| `Common.dll` | Sibling Gmlan Data Logger project (`Common.csproj`) | Carries the `eNodeType` / `eSize` / `eDataType` enums that `BinaryWorker.Header` exposes. Distinct from this repo's own `Common` project (which builds as `EcuSim.Common.dll`). |
| `J2534-Sharp.dll` | Third-party — [github.com/jakka351/J2534-Sharp](https://github.com/jakka351/J2534-Sharp) (and forks) | Transitive dependency of the sibling `Common.dll`. Not directly used by this repo's runtime code — only present so the .NET binder can resolve the reference at load time. |

**Refreshing these DLLs:** rebuild the sibling DataLogger's `BinaryWorker.csproj` in Release mode and copy the three DLLs from its `bin/Release/net9.0-windows/` folder into here. The sibling source is not part of this repo.

**Licence note:** `J2534-Sharp.dll` is third-party. If you fork this repo and intend to redistribute, check the upstream J2534-Sharp licence and add an attribution / NOTICE file as required.
