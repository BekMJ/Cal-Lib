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
var breath = engine.AnalyzeBreath(coRawArray, tempCArray, samplePeriodSec: 0.1, deviceSerial: disSerialString);

// Or if the device provides big-endian byte pairs per CO sample
// e.g., each sample like { 0x01, 0xC5 } for 0x01C5
var breath2 = engine.AnalyzeBreath(coRawBigEndian: beBytePairs, temperatureC: tempCArray, samplePeriodSec: 0.1, deviceSerial: disSerialString);

// Optional alternative: set serial once and keep existing AnalyzeBreath(...) calls unchanged
engine.SetDeviceSerial(disSerialString);

// Optional: inject newer per-device gas-fit constants fetched by the host app
engine.SetPerDeviceGasCalibration(
    disSerialString,
    driftRawPerSec: -2.887169760194338,
    gainRawPerPpm: 18.93036200071086,
    tauSec: 1.0,
    deadTimeSec: 0.0
);
```

### Temperature helper

Convert a 4‑byte temperature reading to Celsius:

```csharp
// Defaults:
// - 2 bytes: assumes 0x2A6E little-endian Int16 (centi-°C) → auto-scales by 0.01
// - 4 bytes: big-endian UInt32 with scale = 1.0
double tC = engine.DecodeTemperatureC(tempBytes);

// Or specify mapping if your sensor uses scale/offset:
// Celsius = offset + scale * raw_uint32
// For 2-byte Int16: 'bigEndian' controls the byte order of the Int16 as well
double tC2 = engine.DecodeTemperatureC(tempBytes, bigEndian: false, scale: 0.01, offset: 0.0);
```

### Baseline (zeroing) and CO ppm from bytes

```csharp
// During Hold Breath (baseline collection)
double avgHoldC = engine.AverageTemperatureCFromBytes(holdTempSamplesBytes); // 5–40 samples typically
engine.SetBaselineFromBytes(holdCoBytes, avgHoldC); // or engine.SetBaseline(coRaw, avgHoldC)

// During Blow Breath (real-time)
double ppm = engine.DecodeCoPpmFromBytes(currentCoBytes, currentTempBytes);
// Track the peak ppm in your app if needed

// To reset
engine.ClearBaseline();
```

Notes:
- CO bytes can be 2 bytes (big-endian ushort) or 4 bytes (uint); both are accepted by SetBaselineFromBytes(...) and DecodeCoPpmFromBytes(...). Default CO endianness for these helpers is big-endian.
- Temperature bytes can be 2 bytes (0x2A6E little-endian Int16 in centi-°C) or 4 bytes (UInt32). The helpers auto-handle 2-byte 0x2A6E. For baseline temperature you may also pass numeric °C via SetBaselineFromBytes(coBytes, averageTempC).

## Calibration (current)

The current managed calibration implements:

1. Breath start: first temp sample ≥ initial temp baseline + 0.8 °C.
2. Baselines:
   - r_base_pre: trimmed mean of pre‑breath CO if ≥ 5 samples; else warmup baseline; else first CO sample.
   - T_base_pre: average of pre‑breath temps; else initial temp baseline.
3. Decision rule:
   - If ΔT > 2.0 °C → human breath path.
   - Else → calibration‑gas exponential fit.

Human breath path (ΔT > 2 °C):
4. Peak CO: r_peak = max raw CO after breath start (no smoothing).
5. Compensated delta:
   - ΔT = T_peak − T_base_pre
   - deltaRComp = (r_peak − r_base_pre) − (17.30 raw/°C) × ΔT − (150.30 raw/V) × ΔV
   - Voltage compensation is fixed at 0.0 in this managed stub (no ΔV input).
6. ppm conversion: ppm = max(0, (deltaRComp − 0.0) / 3.6).

Calibration‑gas path (ΔT ≤ 2 °C):
4. Baseline anchor: first 2 CO samples define a baseline line with device drift.
5. Detect t_start (CO‑based from t=0):
   - ΔCO(t) = CO(t) − [b0 + drift × (t − t0)].
   - Smooth with 3‑sample moving average.
   - Find first index where slope ≥ 0.1 raw/s and ΔCO ≥ 1.0 raw.
   - If not found, t_start = 0 s.
6. Exponential fit over 20 s window starting at t_start:
   - For known device serial prefixes (first 8 alphanumeric chars, uppercased), the SDK uses the bundled per-device table.
   - The host app can inject newer Firestore values with `SetPerDeviceGasCalibration(...)`.
   - For other serials (or unset serial): global constants are used (drift 0, G 0.695, τ 22 s, dead 0 s).
   - Model: y(u) ≈ A(1 − e^(−max(0, u−dead)/τ)), with y drift-adjusted by `− drift*u`.
   - A = Σ y(u) f(u) / Σ f(u)^2, f(u) = 1 − e^(−max(0, u−dead)/τ)
7. ppm conversion: ppm = A / G
8. Gas output quantization:
   - `<2.5 → 0`
   - `2.5..<7.5 → 5`
   - `7.5±0.25 → 7.5` (borderline display)
   - `7.5..<12.5 → 10`
   - `12.5±0.25 → 12.5` (borderline display)
   - `>=12.5 → 15`

Quality/info:
- ShortDuration = true if duration < 5 s
- SmallTemperatureRise = true if ΔT < 1 °C

This remains a placeholder implementation; the API surface is intended to stay stable while coefficients may be refined later.
