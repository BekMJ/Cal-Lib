# Coding Conventions (Quality Focus)

## Scope
- Repository is currently centered on `dotnet/XHale.Core`.
- Primary implementation file is `dotnet/XHale.Core/XHaleEngine.cs`.
- Public contracts are declared in `dotnet/XHale.Core/IXHaleEngine.cs` and `dotnet/XHale.Core/Models.cs`.

## Language and Project Defaults
- C# uses file-scoped namespaces, e.g. `namespace XHale.Core;` in `dotnet/XHale.Core/XHaleEngine.cs`.
- Nullable reference types are enabled (`<Nullable>enable</Nullable>`) in `dotnet/XHale.Core/XHale.Core.csproj`.
- Implicit global usings are enabled (`<ImplicitUsings>enable</ImplicitUsings>`) in `dotnet/XHale.Core/XHale.Core.csproj`.
- Multi-targeting is used: `net9.0;net8.0` in `dotnet/XHale.Core/XHale.Core.csproj`.

## API and Type Design Patterns
- Interface-first API surface: `IXHaleEngine` defines all public operations in `dotnet/XHale.Core/IXHaleEngine.cs`.
- Concrete implementation is `sealed` (`XHaleEngine`) in `dotnet/XHale.Core/XHaleEngine.cs`.
- DTO/result shape uses immutable record semantics: `XHBreathResult` in `dotnet/XHale.Core/Models.cs`.
- Internal calibration payloads use `readonly record struct` for value-like behavior (`DeviceGasCalibration` in `dotnet/XHale.Core/XHaleEngine.cs`).

## Naming Conventions in Practice
- Public members use PascalCase (`Initialize`, `AnalyzeBreath`, `DecodeTemperatureC`) in `dotnet/XHale.Core/IXHaleEngine.cs`.
- Private fields use `_camelCase` (`_samples`, `_licenseKey`) in `dotnet/XHale.Core/XHaleEngine.cs`.
- Constants use `PascalCase` with domain suffixes (`BreathCalibrationTempThresholdC`, `StartSlopeThresholdRawPerSec`) in `dotnet/XHale.Core/XHaleEngine.cs`.
- Method overloads are used heavily for optional context (serial, bytes, endianness) in `dotnet/XHale.Core/IXHaleEngine.cs`.

## Validation and Error-Handling Style
- Guard clauses are preferred at method boundaries in `dotnet/XHale.Core/XHaleEngine.cs`.
- Null checks use `ArgumentNullException` for reference inputs.
- Shape/value checks use `ArgumentException` and `ArgumentOutOfRangeException`.
- Invalid lifecycle state is explicit (`InvalidOperationException` when baseline is unset).
- Fallback behavior is explicit and deterministic (multiple gas-fit fallbacks in `AnalyzeBreath(...)`).

## Algorithm and State Management Conventions
- Session state is mutable but bounded (`_samples` capped by `MaxSamples`) in `dotnet/XHale.Core/XHaleEngine.cs`.
- Computation is decomposed into small private static helpers (`Mean`, `TrimmedMean`, `FindGasStartIndex`).
- Calibration constants are centralized near the top of `dotnet/XHale.Core/XHaleEngine.cs`.
- Device-specific parameters are maintained in one static dictionary (`DeviceGasCalibrations`).

## Documentation and Traceability Patterns
- Complex behavior is documented inline with numbered algorithm comments in `dotnet/XHale.Core/XHaleEngine.cs`.
- Public usage examples and calibration explanation are in `dotnet/XHale.Core/README.md`.
- Packaging metadata is colocated in `dotnet/XHale.Core/XHale.Core.csproj`.
- Publishing automation is documented as executable CI steps in `.github/workflows/publish-nuget.yml`.

## Quality Risks in Current Conventions
- `Initialize(string licenseKey)` stores `_licenseKey` but current code paths do not enforce initialization before analysis in `dotnet/XHale.Core/XHaleEngine.cs`.
- Single-file concentration (`dotnet/XHale.Core/XHaleEngine.cs`) increases change-coupling risk.
- Literal calibration table in code is straightforward but high-maintenance without tests.

## Practical Convention Baseline to Preserve
- Keep guard-clause-first APIs for all new public methods in `dotnet/XHale.Core/IXHaleEngine.cs` and implementation.
- Keep constants and thresholds explicit and named with units in `dotnet/XHale.Core/XHaleEngine.cs`.
- Keep public API semantics documented in `dotnet/XHale.Core/README.md` when behavior changes.
- Keep CI packaging flow in `.github/workflows/publish-nuget.yml` aligned with csproj metadata.
