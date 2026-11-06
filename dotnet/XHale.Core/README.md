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

// Sensor profile (Winsen vs SGX): pass the serial and the DIS Model Number (0x2A24) string together,
// or null for the model if the device has no Model Number characteristic.
engine.SetDeviceIdentity(serial, modelNumberStringOrNull);
// On disconnect:
engine.ClearDeviceIdentity();

// New firmware path: read the 32-byte CAL_READ characteristic from service 0x181A,
// then let the SDK validate CRC/layout and use those constants for gas analysis.
bool accepted = engine.TrySetFirmwareGasCalibrationRecord(calReadBytes);

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
- `DecodeCoPpmFromBytes(...)` is a single-sample helper. For gas-path samples it uses the active calibration gain only; use `AnalyzeBreath(...)` for the full 20-second gas fit with tau, dead time, and drift.

## Sensor profiles (Winsen ME3 vs SGX)

The SDK supports three device profiles, selected from the BLE Device Information **Model Number (0x2A24)**:

| Model Number | Profile | Processing |
|---|---|---|
| *(none)* | nRF + Winsen ME3 (legacy) | Unchanged: gas fit, plus the temperature-triggered human-breath formula |
| *(none)*, serial A793BAFC / D0F18121 / 0DC44112 | nRF + SGX | Known SGX prototypes built before Model Number support |
| `XHale-NRF-ME3` | nRF + Winsen ME3 | Same as legacy |
| `XHale-NRF-SGX` | nRF + SGX | Per-device gas fit at every temperature rise. No Winsen human-breath formula, no Winsen temperature/voltage compensation, never the Winsen generic fallback |
| `XHale-EFR-SGX` | EFR + SGX | Same CO format and gas fit as nRF + SGX; uses only its own firmware CAL_READ or injected calibration (no fallback) |
| anything else | unsupported | No prediction |

Host app responsibilities:

1. Read 0x2A24 after service discovery. Distinguish three outcomes:
   - **Characteristic does not exist** (legacy Winsen firmware): pass `null` as the Model Number.
   - **Read succeeded**: pass the string.
   - **Discovery or read failed** (BLE error/exception): call `SetDeviceIdentity(serial, null, modelNumberLookupFailed: true)` (or `SetModelNumberLookupFailed()`). Never pass `null` for a failed read: `null` means "no Model Number", which selects Winsen and would process a new SGX device with the Winsen formula. A failed lookup blocks prediction ("reconnect and try again"), except for the known SGX prototype serials.
2. Prefer `SetDeviceIdentity(serial, modelNumber[, modelNumberLookupFailed])` once both DIS reads are done, and `ClearDeviceIdentity()` on disconnect (it also clears the firmware calibration). The separate `SetDeviceSerial(...)` / `SetModelNumber(...)` calls also work in either order on a fresh connection; switching from one serial to another clears the previous device's Model Number and firmware calibration.
3. Check `XHBreathResult.PredictionBlocked`. When it is `true`, `EstimatedPPM` is 0 and **must not be shown as a reading**; show `PredictionBlockedReason` instead. Optionally flag readings with `UsedSgxFallbackCalibration = true` as "device not calibrated".
4. `DecodeCoPpmFromBytes(...)` throws `InvalidOperationException` for unsupported devices and for EFR + SGX without calibration. Call `GetSensorProfile()` first if you need to avoid the exception.

### Breath input: sample-only (recommended, matches the iOS app)

Wait 20 s after connecting (sensor warmup), call `SetBaseline(...)` / `SetBaselineFromBytes(...)` with the current CO and temperature, then collect **only the breath sample** (e.g. 20 s) and pass those arrays to `AnalyzeBreath(...)`. With a baseline set, `AnalyzeBreath` treats the arrays as the sample only and uses the iOS app's logic exactly: breath start at the first temperature ≥ mean(first 10 temperatures) + 0.8 °C; pre-breath CO = trimmed mean (≥ 5 samples), else mean of the first 2, else the baseline you set; duration = breath start to last sample; the Winsen duration-bucket fallback uses the sample length.

Without a baseline (legacy), the first 20 s of the arrays are treated as warmup, so the arrays must contain warmup + sample. Passing only a breath sample in legacy mode makes the breath-start threshold and CO baseline come from the breath itself.

`FeedSample(...)` timestamps keep millisecond precision, so `AnalyzeBreath()` derives the real sample period for sub-second sampling.

SGX devices should have device-specific constants: firmware CAL_READ, `SetPerDeviceGasCalibration(...)`, or the bundled table (A793BAFC and D0F18121 are bundled with drift 0, tau 1 s, dead 0). An nRF + SGX device without any of these uses the nRF-SGX fallback gain `XHSensorProfiles.NrfSgxFallbackGainRawPerPpm` (317.55 raw/ppm, the mean of those two app-matched gains; drift 0, tau 1 s, dead 0) and reports `UsedSgxFallbackCalibration = true`; it never uses the Winsen generic profile. EFR + SGX uses only its firmware CAL_READ or injected (`SetPerDeviceGasCalibration`) constants: it never uses the bundled nRF table or a fallback, and without calibration the result is blocked with `MissingGasCalibration = true`. Breath results now also report `SensorProfile` (`nrf_me3`, `nrf_sgx`, `efr_sgx`, `unsupported`) and `SensorProfileSource`.

## Calibration (current)

Everything below describes the legacy nRF + Winsen ME3 profile. SGX profiles skip the human-breath path and always use the calibration-gas fit with device-specific constants.

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
6. ppm conversion:
   - ppm = max(0, (deltaRComp − 63.20735653235655) / 11.558260683760434).

Calibration‑gas path (ΔT ≤ 2 °C):
4. Baseline anchor: first 2 CO samples define a baseline line with device drift.
5. Detect t_start (CO‑based from t=0):
   - ΔCO(t) = CO(t) − [b0 + drift × (t − t0)].
   - Smooth with 3‑sample moving average.
   - Find first index where slope ≥ 0.1 raw/s and ΔCO ≥ 1.0 raw.
   - If not found, t_start = 0 s.
6. Exponential fit over 20 s window starting at t_start:
   - Firmware CAL_READ calibration, if provided through `TrySetFirmwareGasCalibrationRecord(...)`, wins first. It belongs to the device it was read from: `ClearDeviceIdentity()` (call it on disconnect) and switching `SetDeviceIdentity(...)` / `SetDeviceSerial(...)` to a different serial discard it, so a failed CAL_READ on the next device can never reuse the previous device's constants.
   - For known device serial prefixes (first 8 alphanumeric chars, uppercased), the SDK uses injected constants or the bundled per-device table.
   - The host app can inject newer Firestore values with `SetPerDeviceGasCalibration(...)`.
   - Unknown or unset serials with no firmware/injected/table calibration use the SDK's median generic gas profile.
   - Model: y(u) ≈ A(1 − e^(−max(0, u−dead)/τ)), with y drift-adjusted by `− drift*u`.
   - A = Σ y(u) f(u) / Σ f(u)^2, f(u) = 1 − e^(−max(0, u−dead)/τ)
7. ppm conversion:
   - calibrated path: ppm = A / G
   - unknown / unset serials without calibration: ppm is computed with the median generic profile and `UsedGenericGasCalibration` is true.
8. Gas output quantization:
   - `2.0...7.0 → 5`
   - `8.0...12.0 → 10`
   - `13.0...17.0 → 15`
   - otherwise the continuous ppm is returned.

Quality/info:
- ShortDuration = true if duration < 5 s
- SmallTemperatureRise = true if ΔT < 1 °C
- MissingGasCalibration is retained for API compatibility; with the generic fallback it should normally stay false.
- UsedGenericGasCalibration = true when gas-path raw CO used the SDK's median fallback instead of firmware, injected, or bundled serial constants.

This remains a placeholder implementation; the API surface is intended to stay stable while coefficients may be refined later.
