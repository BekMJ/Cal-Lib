# Architecture Map

## Scope
- Repository centers on a single .NET class library: `dotnet/XHale.Core`.
- Build outputs and packaged artifacts are versioned into `artifacts/`.
- CI/CD for package publishing is defined in `.github/workflows/publish.yml`.

## System Context
- Primary consumer model is external applications importing a NuGet package (`XHale.Core`).
- Runtime architecture is in-process only; there are no network services, daemons, or persistence layers in this repo.
- The package is intended as a cross-platform calibration/analysis facade for client apps.

## Architectural Style
- `dotnet/XHale.Core` uses a layered, library-first structure:
- Public contract layer: `dotnet/XHale.Core/IXHaleEngine.cs`.
- Domain/result model layer: `dotnet/XHale.Core/Models.cs`.
- Implementation layer: `dotnet/XHale.Core/XHaleEngine.cs`.
- Distribution metadata layer: `dotnet/XHale.Core/XHale.Core.csproj`, `dotnet/XHale.Core/README.md`.

## Core Components
- `IXHaleEngine` (`dotnet/XHale.Core/IXHaleEngine.cs`): stable API surface for initialization, session ingestion, batch analysis, byte decoding, and baseline handling.
- `XHaleEngine` (`dotnet/XHale.Core/XHaleEngine.cs`): stateful implementation with sample buffering, breath/gas path decision logic, fitting helpers, and calibration tables.
- `XHBreathResult` (`dotnet/XHale.Core/Models.cs`): immutable output record for analysis response.

## Data and Control Flow
1. Consumer initializes engine via `Initialize(...)`.
2. Consumer either streams samples (`FeedSample(...)`) or passes arrays (`AnalyzeBreath(...)` overloads).
3. Engine computes baseline/warmup and detects start event.
4. Engine selects path:
- human breath path when temperature rise exceeds threshold;
- gas-fit path otherwise, with per-device or global calibration.
5. Engine quantizes/returns result as `XHBreathResult`.
6. Optional baseline APIs enable byte-oriented real-time ppm decode flows.

## State Model
- Instance state is ephemeral and memory-only:
- `_samples` stores recent session points and is bounded (`MaxSamples`).
- `_baselineExplicitCoRaw` and `_baselineExplicitTempC` represent hold-breath zeroing state.
- `_deviceSerialPrefix` selects device-specific calibration constants.
- No filesystem/database persistence in domain code.

## Dependency Profile
- No third-party NuGet dependencies in `dotnet/XHale.Core/XHale.Core.csproj`.
- Depends on BCL namespaces only (`System`, `System.Collections.Generic`, `System.Linq`).
- Package build metadata is embedded in the project file (ID/version/readme).

## Build and Release Architecture
- Project targets `net9.0` and `net8.0` in `dotnet/XHale.Core/XHale.Core.csproj`.
- Release packaging command: `dotnet pack ./dotnet/XHale.Core/XHale.Core.csproj -c Release -o ./artifacts`.
- GitHub Actions workflow `.github/workflows/publish.yml` runs restore, pack, and pushes `.nupkg` to GitHub Packages on `v*` tags.
- Local artifact history is retained in `artifacts/` (`XHale.Core.0.1.0-alpha.*.nupkg`).

## Boundary Notes
- Interface and implementation are colocated in one project; no separate test, adapter, or infrastructure project exists yet.
- Engine combines orchestration + numerical helpers in one file (`XHaleEngine.cs`), so domain decomposition is functional rather than per-class.
- Calibration constants and device tables are static in code, not configuration-driven.

## Architectural Risks / Tradeoffs
- Single large implementation file increases change coupling and review load.
- In-memory statefulness implies instance lifecycle discipline is required by consumers.
- Versioned artifacts are committed, which improves traceability but can inflate repository size over time.
- CI publishes from tags only; no staged validation pipeline in this repository.

## Practical Extension Points
- Add new device calibrations in `DeviceGasCalibrations` within `dotnet/XHale.Core/XHaleEngine.cs`.
- Expand API in `dotnet/XHale.Core/IXHaleEngine.cs` while preserving existing method contracts.
- Split computation helpers from session orchestration into additional files under `dotnet/XHale.Core/` if maintainability becomes a concern.
