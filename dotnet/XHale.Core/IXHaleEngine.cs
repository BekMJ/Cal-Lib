using System;

namespace XHale.Core;

public interface IXHaleEngine
{
    void Initialize(string licenseKey);
    // Optional: set BLE serial so known devices can use per-device gas-fit constants.
    // Pass null/empty to clear and use global calibration.
    void SetDeviceSerial(string? serial);
    void ResetSession();
    void FeedSample(double coRaw, double temperatureC, double humidityPct, DateTimeOffset timestamp);
    XHBreathResult AnalyzeBreath();
    XHBreathResult AnalyzeBreath(string? deviceSerial);

    // Batch analysis: arrays of raw CO and temperature with a fixed sample period (seconds)
    XHBreathResult AnalyzeBreath(double[] coRaw, double[] temperatureC, double samplePeriodSec);
    XHBreathResult AnalyzeBreath(double[] coRaw, double[] temperatureC, double samplePeriodSec, string? deviceSerial);

    // Batch analysis: arrays of big-endian CO bytes (each sample is 2 bytes: MSB, LSB)
    XHBreathResult AnalyzeBreath(byte[][] coRawBigEndian, double[] temperatureC, double samplePeriodSec);
    XHBreathResult AnalyzeBreath(byte[][] coRawBigEndian, double[] temperatureC, double samplePeriodSec, string? deviceSerial);

    // Decode a temperature reading into Celsius.
    // Accepts 2 bytes (int16) or 4 bytes (uint32).
    // Defaults: if 2 bytes, assumes standard 0x2A6E little-endian int16 in centi-°C; if 4 bytes, big-endian with scale=1, offset=0.
    double DecodeTemperatureC(byte[] temperatureBytes);

    // Decode with explicit mapping: Celsius = offset + scale * raw.
    // If temperatureBytes.Length == 2, the raw is interpreted as signed Int16 using the provided endianness.
    double DecodeTemperatureC(byte[] temperatureBytes, bool bigEndian, double scale, double offset);

    // --- Baseline (zeroing) APIs ---
    // Explicitly set/clear baseline values (Hold Breath)
    void SetBaseline(double coRaw, double tempC);
    void SetBaselineFromBytes(byte[] coBytes, byte[] temperatureBytes);
    void SetBaselineFromBytes(byte[] coBytes, byte[] temperatureBytes, bool bigEndian);
    // New: CO from bytes + numeric average Temp (°C)
    void SetBaselineFromBytes(byte[] coBytes, double averageTempC);
    void SetBaselineFromBytes(byte[] coBytes, double averageTempC, bool bigEndian);
    void ClearBaseline();

    // Decode CO PPM from current sample bytes using stored baseline and temperature compensation
    // Uses: ΔR = CO_raw - CO_raw_baseline; ΔT = T - T_baseline; ppm = max(0, (ΔR - 17.30*ΔT) / 3.60)
    double DecodeCoPpmFromBytes(byte[] coBytes, byte[] temperatureBytes);
    double DecodeCoPpmFromBytes(byte[] coBytes, byte[] temperatureBytes, bool bigEndian);

    // Average temperature helper for Hold Breath step (expects 5–40 samples typically)
    double AverageTemperatureCFromBytes(System.Collections.Generic.IEnumerable<byte[]> temperatureSamplesBytes);
    double AverageTemperatureCFromBytes(System.Collections.Generic.IEnumerable<byte[]> temperatureSamplesBytes, bool bigEndian);
}

