# XHale.Core (stub)

This is a stub managed library exposing a minimal API for calibration/battery flows. It targets:

- net9.0
- net8.0

This stub uses pure managed logic and includes no native assets.

## Public API

- `IXHaleEngine`
- `XHaleEngine` (stub implementation)
- `XHBatteryInfo`, `XHBreathResult`

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
engine.FeedSample(coRaw: 1.2, temperatureC: 25.1, humidityPct: 40.0, timestamp: DateTimeOffset.UtcNow);
var battery = engine.ComputeBatteryFromRawADC(2048);
var breath = engine.AnalyzeBreath();
```

Note: This is a placeholder implementation; values are not final. The API surface is stable and will remain compatible when native algorithms are introduced.
