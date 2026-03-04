# Testing Patterns (Quality Focus)

## Current State
- No dedicated test project is present under `dotnet/` (no `*Tests.csproj` detected).
- No solution file was detected at repository root (no `.sln` file detected).
- CI only packages/publishes; there is no `dotnet test` step in `.github/workflows/publish-nuget.yml`.
- Repository currently relies on manual verification patterns in `dotnet/XHale.Core/README.md` usage examples.

## Existing Quality Signals
- Extensive input validation in `dotnet/XHale.Core/XHaleEngine.cs` creates natural unit-test seams.
- Deterministic helper functions (`Mean`, `TrimmedMean`, `QuantizeGasPpm`) enable fast pure unit tests.
- Public API is centralized in `dotnet/XHale.Core/IXHaleEngine.cs`, making contract-level tests straightforward.
- Core domain behavior (calibration thresholds and quantization) is encoded as constants in `dotnet/XHale.Core/XHaleEngine.cs`.

## Practical Test Targets by File
- `dotnet/XHale.Core/XHaleEngine.cs`
- `dotnet/XHale.Core/IXHaleEngine.cs`
- `dotnet/XHale.Core/Models.cs`
- `.github/workflows/publish-nuget.yml`

## Suggested Test Structure
- Add `dotnet/XHale.Core.Tests/XHale.Core.Tests.csproj` targeting `net8.0` (and optionally `net9.0`).
- Reference `dotnet/XHale.Core/XHale.Core.csproj` from the test project.
- Keep tests organized by API surface:
- `dotnet/XHale.Core.Tests/AnalyzeBreathTests.cs`
- `dotnet/XHale.Core.Tests/ByteDecodeTests.cs`
- `dotnet/XHale.Core.Tests/BaselineTests.cs`
- `dotnet/XHale.Core.Tests/QuantizationTests.cs`

## High-Value Unit Test Patterns
- Guard tests: null/length/range errors for overloads in `AnalyzeBreath(...)` and decode methods.
- Boundary tests: `BreathCalibrationTempThresholdC`, `GasThresholdZeroToFivePpm`, `GasThresholdFiveToTenPpm`, `GasThresholdTenToFifteenPpm` behavior in `dotnet/XHale.Core/XHaleEngine.cs`.
- Endianness tests: 2-byte and 4-byte decode variants for `DecodeTemperatureC(...)` and CO byte decoding paths.
- Lifecycle tests: baseline required before `DecodeCoPpmFromBytes(...)`; baseline clear/reset behavior.
- Session tests: `FeedSample(...)` cap behavior and `ResetSession()` semantics.

## Calibration Regression Pattern
- Add fixture-based regression vectors in `dotnet/XHale.Core.Tests/TestData/`.
- Include representative device serial prefixes from `DeviceGasCalibrations` in `dotnet/XHale.Core/XHaleEngine.cs`.
- Validate expected discretized outputs (`0`, `5`, `7.5`, `10`, `12.5`, `15`) for gas-fit path.
- Validate human-path behavior when `deltaT > 2.0`.

## CI Testing Pattern to Introduce
- Add a dedicated CI workflow under `.github/workflows/` that runs:
- `dotnet restore`
- `dotnet build -c Release`
- `dotnet test -c Release --no-build`
- Keep `dotnet pack` and publish in `.github/workflows/publish-nuget.yml` gated on passing tests.

## Coverage Expectations (Practical)
- Prioritize branch coverage around threshold and fallback logic in `dotnet/XHale.Core/XHaleEngine.cs`.
- Ensure every public method in `dotnet/XHale.Core/IXHaleEngine.cs` has at least one success and one failure-path test.
- Track regressions for calibration table changes by snapshotting expected outputs for known inputs.

## Minimal Acceptance Bar
- Tests run locally with one command from repo root (`dotnet test`).
- CI fails on test failures before package publication.
- New calibration constants or device entries require test fixture updates in the same change.
