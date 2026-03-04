# Repository Structure Map

## Top-Level Layout
- `.github/`: CI automation for package publishing.
- `.planning/`: planning workspace; output docs live in `.planning/codebase/`.
- `artifacts/`: packaged NuGet outputs (`.nupkg`) for released alpha builds.
- `dotnet/`: source tree root for .NET code.

## Source Tree Breakdown
- `dotnet/XHale.Core/XHale.Core.csproj`: project definition, target frameworks, package metadata.
- `dotnet/XHale.Core/IXHaleEngine.cs`: primary public interface (contract-first entry point).
- `dotnet/XHale.Core/XHaleEngine.cs`: concrete engine implementation and algorithmic helpers.
- `dotnet/XHale.Core/Models.cs`: shared model record(s) returned to consumers.
- `dotnet/XHale.Core/README.md`: package-level usage and calibration notes.

## Build/Distribution Structure
- `.github/workflows/publish.yml`: GitHub Actions workflow that restores, packs, and pushes package artifacts.
- `artifacts/XHale.Core.0.1.0-alpha.1.nupkg` ... `artifacts/XHale.Core.0.1.0-alpha.6.nupkg`: historical package snapshots.
- `dotnet/XHale.Core/obj/`: generated restore/build intermediates (`project.assets.json`, NuGet props/targets).

## Internal Code Organization (XHale.Core)
- Public API signatures are centralized in `IXHaleEngine.cs`.
- Result model type is minimal and isolated in `Models.cs`.
- `XHaleEngine.cs` organizes logic into:
- public API implementation methods (`Initialize`, `FeedSample`, `AnalyzeBreath`, decoding, baseline ops);
- byte conversion helpers (`DecodeUInt32FromBytes`, `DecodeInt16FromBytes`, `DecodeCoRawFromBytes`);
- numeric/statistical helpers (`Mean`, `TrimmedMean`, `ComputeSampleCountForSeconds`);
- signal and fitting helpers (`FindBreathStartIndex`, `FindGasStartIndex`, `TryFitGasPpm`, `QuantizeGasPpm`);
- calibration data types and tables (`DeviceGasCalibration`, `DeviceGasCalibrations`).

## Configuration and Metadata Files
- `.gitignore`: repository ignore rules.
- `dotnet/XHale.Core/XHale.Core.csproj`: includes package readme and semantic version (`0.1.0-alpha.6`).
- `.github/workflows/publish.yml`: pipeline trigger strategy (`push` tags `v*`, plus manual dispatch).

## Packaging and Versioning Signals
- Git tags in repo include `v0.1.0-alpha.1` and `v0.1.0-alpha.2`.
- NuGet package versions in `artifacts/` indicate progression up to `0.1.0-alpha.6`.
- Project file version aligns with latest artifact version.

## What Is Not Present
- No solution file (`.sln`) at repository root.
- No dedicated test project directories (e.g., `tests/` or `*.Tests.csproj`).
- No multi-package monorepo structure; only one production library project is present.
- No app host (web/api/console) in this repository.

## Practical Navigation Guide
- Start architecture review at `dotnet/XHale.Core/IXHaleEngine.cs` for contract scope.
- Move to `dotnet/XHale.Core/XHaleEngine.cs` for behavior and calibration implementation details.
- Validate packaging/release behavior in `.github/workflows/publish.yml` and `dotnet/XHale.Core/XHale.Core.csproj`.
- Inspect generated build state only when troubleshooting toolchain issues (`dotnet/XHale.Core/obj/*`).

## Mapper Notes
- Codebase is compact and single-purpose; architecture and structure are straightforward.
- Most future structural growth will likely come from adding tests, splitting engine modules, or introducing additional project(s) under `dotnet/`.
- Current layout is optimized for publishing a single reusable NuGet library.
