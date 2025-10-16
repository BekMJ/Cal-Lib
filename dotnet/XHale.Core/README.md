# XHale.Core (stub)

This is a stub managed library exposing a minimal API for calibration/battery flows. It targets:

- net9.0
- net8.0

This stub uses pure managed logic and includes no native assets.

## Public API

- `IXHaleEngine`
- `XHaleEngine` (stub implementation)
- `XHBreathResult`

## Build and pack

```
# From repo root
 dotnet pack ./dotnet/XHale.Core/XHale.Core.csproj -c Release -o ./artifacts
```

The resulting `.nupkg` will be in `./artifacts`. Share that file directly for local import.

## Usage

```csharp
var engine = new XHale.Core.XHaleEngine();
engine.Initialize("test-key");
// Batch arrays (uniform sampling)
var breath = engine.AnalyzeBreath(coRawArray, tempCArray, samplePeriodSec: 0.1);

// Or if the device provides big-endian byte pairs per CO sample
// e.g., each sample like { 0x01, 0xC5 } for 0x01C5
var breath2 = engine.AnalyzeBreath(coRawBigEndian: beBytePairs, temperatureC: tempCArray, samplePeriodSec: 0.1);
```

Note: This is a placeholder implementation; values are not final. The API surface is stable and will remain compatible when native algorithms are introduced.
