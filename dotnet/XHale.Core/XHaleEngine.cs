using System;
using System.Collections.Generic;
using System.Linq;

namespace XHale.Core;

public sealed class XHaleEngine : IXHaleEngine
{
    private string? _licenseKey;
    private string? _deviceSerialPrefix;

    private readonly List<Sample> _samples = new(capacity: 1024);

    private const int MaxSamples = 4096;
    // Calibration (current):
    // 1) Breath start (temp): first temp sample >= initial temp baseline + 0.8°C.
    // 2) r_base_pre: trimmed mean of pre-breath CO if >=5 samples; else warmup baseline; else first CO sample.
    // 3) T_base_pre: average of pre-breath temps; else initial temp baseline.
    // 4) r_peak (human path): max raw CO after breath start (no smoothing).
    // 5) ΔT = T_peak - T_base_pre.
    // 6) Decision rule: if ΔT > 2.0°C use human-breath calibration; else gas-fit calibration.
    // 7) Human path: ppm = max(0, (ΔR_comp - intercept) / slope).
    // 8) Gas path: fit exponential amplitude on drift-compensated signal over 20s after detected start.
    // 9) Gas output is discretized to 0/5/10/15 ppm with borderline display points 7.5 and 12.5.
    // 10) Quality gates: ShortDuration (<2s), SmallTemperatureRise (ΔT < 2°C).
    private const double CalibrationGasFallbackSlopeRawPerPpm = 0.98;
    private const double CalibrationGasFallbackInterceptRaw = -1.8;
    private const double BreathCalibrationSlopeRawPerPpm = 3.60;
    private const double BreathCalibrationInterceptRaw = 0.0;
    private const double BreathCalibrationTempThresholdC = 2.0;
    private const double BreathStartTempRiseC = 0.8;
    private const double TemperatureCompensationRawPerC = 17.30;
    private const double VoltageCompensationRawPerV = 150.30;
    private const double WarmupDurationSec = 20.0;
    private const double FitWindowSec = 20.0;
    private const double GasGainRawPerPpm = 0.695;
    private const double GasTauSec = 22.0;
    private const double StartSlopeThresholdRawPerSec = 0.1;
    private const double StartMinDeltaRaw = 1.0;
    private const double GasThresholdZeroToFivePpm = 2.5;
    private const double GasThresholdFiveToTenPpm = 7.5;
    private const double GasThresholdTenToFifteenPpm = 12.5;
    private const double GasThresholdBorderlineBandPpm = 0.25;
    private const double TrimFraction = 0.10;
    private static readonly Dictionary<string, DeviceGasCalibration> DeviceGasCalibrations = new(StringComparer.Ordinal)
    {
        // Existing legacy table
        ["6C8A4BC7"] = new DeviceGasCalibration(-0.0227256, 0.798849, 34.25, 5.5),
        ["D1A07CD4"] = new DeviceGasCalibration(-0.0637795, 0.653858, 14.5, 1.4),
        ["D92EC0CB"] = new DeviceGasCalibration(-0.0401157, 0.724937, 19.5, 4.0),
        ["F2E4CB88"] = new DeviceGasCalibration(-0.0314408, 0.697511, 19.5, 3.3),
        ["F685F16F"] = new DeviceGasCalibration(-0.0333294, 0.692745, 24.5, 5.6),
        // 0/5/10/15 campaign devices
        ["36F14E25"] = new DeviceGasCalibration(-2.089954189448795, 74.80982480894498, 22.0, 3.0),
        ["4F2F6B63"] = new DeviceGasCalibration(-2.1033249593616072, 39.7056459294788, 22.0, 3.0),
        ["9E9F6459"] = new DeviceGasCalibration(-1.780570415250479, 59.16004336321591, 22.0, 3.0),
        ["B73545B1"] = new DeviceGasCalibration(-1.9470696024826366, 80.7635699785618, 22.0, 3.0),
        ["7FF4CB9D"] = new DeviceGasCalibration(-2.928596389050625, 68.33999181557918, 22.0, 3.0),
        ["E0AED989"] = new DeviceGasCalibration(-3.1222597553873244, 61.93527632478544, 22.0, 3.0),
        ["E5ACF73C"] = new DeviceGasCalibration(-3.4828421665696094, 79.87938353276542, 22.0, 3.0),
        ["F7CF3358"] = new DeviceGasCalibration(-1.703436225975285, 55.52555675050862, 22.0, 3.0),
    };
    private double? _baselineExplicitCoRaw;
    private double? _baselineExplicitTempC;

    public void Initialize(string licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new ArgumentException("License key must be non-empty", nameof(licenseKey));
        }
        _licenseKey = licenseKey;
    }

    public void SetDeviceSerial(string? serial)
    {
        _deviceSerialPrefix = NormalizeSerialPrefix(serial);
    }

    public void ResetSession()
    {
        _samples.Clear();
    }

    // Battery computation removed per request

    public void FeedSample(double coRaw, double temperatureC, double humidityPct, DateTimeOffset timestamp)
    {
        double ts = timestamp.ToUnixTimeSeconds();
        _samples.Add(new Sample(coRaw, temperatureC, humidityPct, ts));
        if (_samples.Count > MaxSamples)
        {
            int removeCount = _samples.Count - MaxSamples;
            _samples.RemoveRange(0, removeCount);
        }
    }

    public XHBreathResult AnalyzeBreath()
    {
        return AnalyzeBreath(deviceSerial: null);
    }

    public XHBreathResult AnalyzeBreath(string? deviceSerial)
    {
        if (_samples.Count < 2)
        {
            return new XHBreathResult(0, 0, true, true);
        }

        int n = _samples.Count;
        double[] co = new double[n];
        double[] t = new double[n];
        for (int i = 0; i < n; i++)
        {
            co[i] = _samples[i].CoRaw;
            t[i] = _samples[i].TemperatureC;
        }

        // Estimate sample period as median of deltas (robust to jitter)
        var deltas = new List<double>(capacity: Math.Max(0, n - 1));
        for (int i = 1; i < n; i++)
        {
            double dt = _samples[i].TimestampS - _samples[i - 1].TimestampS;
            if (dt > 0) deltas.Add(dt);
        }
        double samplePeriodSec = 0.1;
        if (deltas.Count > 0)
        {
            deltas.Sort();
            samplePeriodSec = deltas[deltas.Count / 2];
        }

        return AnalyzeBreath(co, t, samplePeriodSec, deviceSerial);
    }

    public XHBreathResult AnalyzeBreath(double[] coRaw, double[] temperatureC, double samplePeriodSec)
    {
        return AnalyzeBreath(coRaw, temperatureC, samplePeriodSec, deviceSerial: null);
    }

    public XHBreathResult AnalyzeBreath(double[] coRaw, double[] temperatureC, double samplePeriodSec, string? deviceSerial)
    {
        if (coRaw == null || temperatureC == null) throw new ArgumentNullException("Input arrays cannot be null");
        if (coRaw.Length == 0 || temperatureC.Length == 0) return new XHBreathResult(0, 0, true, true);
        if (coRaw.Length != temperatureC.Length) throw new ArgumentException("Array lengths must match");
        if (samplePeriodSec <= 0) throw new ArgumentOutOfRangeException(nameof(samplePeriodSec));

        int n = coRaw.Length;

        int warmupSampleCount = ComputeSampleCountForSeconds(WarmupDurationSec, samplePeriodSec, n);
        double warmupCoBaseline = Mean(coRaw, 0, warmupSampleCount);
        double warmupTempBaseline = Mean(temperatureC, 0, warmupSampleCount);

        int breathStartIdx = FindBreathStartIndex(temperatureC, warmupTempBaseline);
        double tBasePre = breathStartIdx > 0
            ? Mean(temperatureC, 0, breathStartIdx)
            : warmupTempBaseline;

        double rBasePre;
        if (breathStartIdx >= 5)
        {
            rBasePre = TrimmedMean(coRaw, 0, breathStartIdx);
        }
        else if (warmupSampleCount > 0)
        {
            rBasePre = warmupCoBaseline;
        }
        else
        {
            rBasePre = coRaw[0];
        }

        int peakIdx = breathStartIdx;
        double peakVal = coRaw[peakIdx];
        for (int i = breathStartIdx + 1; i < n; i++)
        {
            if (coRaw[i] > peakVal)
            {
                peakVal = coRaw[i];
                peakIdx = i;
            }
        }

        double duration = Math.Max(0, (peakIdx - breathStartIdx) * samplePeriodSec);
        double rPeak = peakVal;
        double tPeak = temperatureC[peakIdx];
        double deltaT = tPeak - tBasePre;

        bool useBreathCalibration = deltaT > BreathCalibrationTempThresholdC;
        double estimatedPpm;
        if (useBreathCalibration)
        {
            double deltaV = 0.0;
            double deltaRComp = (rPeak - rBasePre)
                - (TemperatureCompensationRawPerC * deltaT)
                - (VoltageCompensationRawPerV * deltaV);
            estimatedPpm = Math.Max(0, (deltaRComp - BreathCalibrationInterceptRaw) / BreathCalibrationSlopeRawPerPpm);
        }
        else
        {
            var calibration = GetGasCalibrationForDevice(deviceSerial);
            if (TryFitGasPpm(coRaw, samplePeriodSec, calibration, out double gasPpm, out double fitDuration))
            {
                duration = fitDuration;
                estimatedPpm = QuantizeGasPpm(Math.Max(0, gasPpm));
            }
            else if (TryFitGasPpm(
                coRaw,
                samplePeriodSec,
                DeviceGasCalibration.Global(GasGainRawPerPpm, GasTauSec),
                out double fallbackGasPpm,
                out double fallbackFitDuration))
            {
                duration = fallbackFitDuration;
                estimatedPpm = QuantizeGasPpm(Math.Max(0, fallbackGasPpm));
            }
            else
            {
                // Preserve older fallback behavior when fit is not computable.
                double deltaV = 0.0;
                double deltaRComp = (rPeak - rBasePre)
                    - (TemperatureCompensationRawPerC * deltaT)
                    - (VoltageCompensationRawPerV * deltaV);
                double fallbackPpm = Math.Max(0, (deltaRComp - CalibrationGasFallbackInterceptRaw) / CalibrationGasFallbackSlopeRawPerPpm);
                estimatedPpm = QuantizeGasPpm(fallbackPpm);
            }
        }

        bool shortDuration = duration < 2.0;
        bool smallTempRise = deltaT < BreathCalibrationTempThresholdC;

        return new XHBreathResult(estimatedPpm, duration, shortDuration, smallTempRise);
    }

    public XHBreathResult AnalyzeBreath(byte[][] coRawBigEndian, double[] temperatureC, double samplePeriodSec)
    {
        return AnalyzeBreath(coRawBigEndian, temperatureC, samplePeriodSec, deviceSerial: null);
    }

    public XHBreathResult AnalyzeBreath(byte[][] coRawBigEndian, double[] temperatureC, double samplePeriodSec, string? deviceSerial)
    {
        if (coRawBigEndian == null || temperatureC == null) throw new ArgumentNullException("Input arrays cannot be null");
        if (coRawBigEndian.Length == 0 || temperatureC.Length == 0) return new XHBreathResult(0, 0, true, true);
        if (coRawBigEndian.Length != temperatureC.Length) throw new ArgumentException("Array lengths must match");
        if (samplePeriodSec <= 0) throw new ArgumentOutOfRangeException(nameof(samplePeriodSec));

        // Convert big-endian byte pairs to integer values (unsigned)
        int n = coRawBigEndian.Length;
        double[] coRaw = new double[n];
        for (int i = 0; i < n; i++)
        {
            var bytes = coRawBigEndian[i];
            if (bytes == null || bytes.Length != 2)
            {
                throw new ArgumentException("Each CO sample must be exactly 2 bytes (big-endian)");
            }
            int value = (bytes[0] << 8) | bytes[1];
            coRaw[i] = value;
        }

        return AnalyzeBreath(coRaw, temperatureC, samplePeriodSec, deviceSerial);
    }

    public double DecodeTemperatureC(byte[] temperatureBytes)
    {
        if (temperatureBytes == null) throw new ArgumentNullException(nameof(temperatureBytes));
        if (temperatureBytes.Length == 2)
        {
            // Default: GATT 0x2A6E little-endian int16 in centi-°C
            short rawS16 = DecodeInt16FromBytes(temperatureBytes, bigEndian: false);
            return 0.0 + 0.01 * rawS16;
        }
        if (temperatureBytes.Length == 4)
        {
            // Default: treat as big-endian uint32 with scale 1.0
            uint raw = DecodeUInt32FromBytes(temperatureBytes, bigEndian: true);
            return 0.0 + 1.0 * raw;
        }
        throw new ArgumentException("Temperature bytes must be exactly 2 or 4 bytes", nameof(temperatureBytes));
    }

    public double DecodeTemperatureC(byte[] temperatureBytes, bool bigEndian, double scale, double offset)
    {
        if (temperatureBytes == null) throw new ArgumentNullException(nameof(temperatureBytes));
        if (temperatureBytes.Length == 2)
        {
            short rawS16 = DecodeInt16FromBytes(temperatureBytes, bigEndian);
            return offset + scale * rawS16;
        }
        if (temperatureBytes.Length == 4)
        {
            uint raw = DecodeUInt32FromBytes(temperatureBytes, bigEndian);
            return offset + scale * raw;
        }
        throw new ArgumentException("Temperature bytes must be exactly 2 or 4 bytes", nameof(temperatureBytes));
    }

    public void SetBaseline(double coRaw, double tempC)
    {
        _baselineExplicitCoRaw = coRaw;
        _baselineExplicitTempC = tempC;
    }

    public void SetBaselineFromBytes(byte[] coBytes, byte[] temperatureBytes)
    {
        SetBaselineFromBytes(coBytes, temperatureBytes, bigEndian: true);
    }

    public void SetBaselineFromBytes(byte[] coBytes, byte[] temperatureBytes, bool bigEndian)
    {
        if (coBytes == null) throw new ArgumentNullException(nameof(coBytes));
        if (temperatureBytes == null) throw new ArgumentNullException(nameof(temperatureBytes));

        uint coRaw = DecodeCoRawFromBytes(coBytes, bigEndian);
        // Temperature: if 2 bytes, assume GATT 0x2A6E (little-endian centi-°C); if 4 bytes, default big-endian
        double tempC = DecodeTemperatureC(temperatureBytes);
        SetBaseline(coRaw, tempC);
    }

    public void SetBaselineFromBytes(byte[] coBytes, double averageTempC)
    {
        SetBaselineFromBytes(coBytes, averageTempC, bigEndian: true);
    }

    public void SetBaselineFromBytes(byte[] coBytes, double averageTempC, bool bigEndian)
    {
        if (coBytes == null) throw new ArgumentNullException(nameof(coBytes));
        uint coRaw = DecodeCoRawFromBytes(coBytes, bigEndian);
        SetBaseline(coRaw, averageTempC);
    }

    public void ClearBaseline()
    {
        _baselineExplicitCoRaw = null;
        _baselineExplicitTempC = null;
    }

    public double DecodeCoPpmFromBytes(byte[] coBytes, byte[] temperatureBytes)
    {
        return DecodeCoPpmFromBytes(coBytes, temperatureBytes, bigEndian: true);
    }

    public double DecodeCoPpmFromBytes(byte[] coBytes, byte[] temperatureBytes, bool bigEndian)
    {
        if (_baselineExplicitCoRaw == null || _baselineExplicitTempC == null)
        {
            throw new InvalidOperationException("Baseline not set. Call SetBaseline(...) before decoding ppm.");
        }
        if (coBytes == null) throw new ArgumentNullException(nameof(coBytes));
        if (temperatureBytes == null) throw new ArgumentNullException(nameof(temperatureBytes));

        uint coRaw = DecodeCoRawFromBytes(coBytes, bigEndian);
        double tempC = DecodeTemperatureC(temperatureBytes);

        double deltaR = coRaw - _baselineExplicitCoRaw.Value;
        double deltaT = tempC - _baselineExplicitTempC.Value;
        double compensatedDeltaR = deltaR - (TemperatureCompensationRawPerC * deltaT);

        bool useBreathCalibration = deltaT > BreathCalibrationTempThresholdC;
        double slope = useBreathCalibration ? BreathCalibrationSlopeRawPerPpm : CalibrationGasFallbackSlopeRawPerPpm;
        double intercept = useBreathCalibration ? BreathCalibrationInterceptRaw : CalibrationGasFallbackInterceptRaw;
        return Math.Max(0, (compensatedDeltaR - intercept) / slope);
    }

    public double AverageTemperatureCFromBytes(IEnumerable<byte[]> temperatureSamplesBytes)
    {
        return AverageTemperatureCFromBytes(temperatureSamplesBytes, bigEndian: true);
    }

    public double AverageTemperatureCFromBytes(IEnumerable<byte[]> temperatureSamplesBytes, bool bigEndian)
    {
        if (temperatureSamplesBytes == null) throw new ArgumentNullException(nameof(temperatureSamplesBytes));
        double sum = 0;
        int count = 0;
        foreach (var sample in temperatureSamplesBytes)
        {
            if (sample == null || (sample.Length != 2 && sample.Length != 4))
            {
                throw new ArgumentException("Each temperature sample must be exactly 2 or 4 bytes");
            }
            if (sample.Length == 2)
            {
                // Default to 0x2A6E little-endian centi-°C
                sum += DecodeTemperatureC(sample);
            }
            else
            {
                sum += DecodeTemperatureC(sample, bigEndian, scale: 1.0, offset: 0.0);
            }
            count++;
        }
        if (count == 0) throw new ArgumentException("No temperature samples provided");
        return sum / count;
    }

    private static uint DecodeUInt32FromBytes(byte[] bytes, bool bigEndian)
    {
        uint b0 = bytes[0];
        uint b1 = bytes[1];
        uint b2 = bytes[2];
        uint b3 = bytes[3];
        return bigEndian
            ? ((b0 << 24) | (b1 << 16) | (b2 << 8) | b3)
            : ((b3 << 24) | (b2 << 16) | (b1 << 8) | b0);
    }

    private static short DecodeInt16FromBytes(byte[] bytes, bool bigEndian)
    {
        if (bytes.Length != 2) throw new ArgumentException("Requires 2 bytes", nameof(bytes));
        int value = bigEndian ? ((bytes[0] << 8) | bytes[1]) : ((bytes[1] << 8) | bytes[0]);
        return unchecked((short)value);
    }
    // CO raw: accept 2-byte (ushort) or 4-byte (uint) payloads
    private static uint DecodeCoRawFromBytes(byte[] bytes, bool bigEndian)
    {
        if (bytes.Length == 2)
        {
            return bigEndian ? (uint)((bytes[0] << 8) | bytes[1]) : (uint)((bytes[1] << 8) | bytes[0]);
        }
        if (bytes.Length == 4)
        {
            return DecodeUInt32FromBytes(bytes, bigEndian);
        }
        throw new ArgumentException("CO bytes must be exactly 2 or 4 bytes", nameof(bytes));
    }

    private static int ComputeSampleCountForSeconds(double seconds, double samplePeriodSec, int maxCount)
    {
        int count = (int)Math.Round(seconds / samplePeriodSec);
        count = Math.Max(1, count);
        return Math.Min(count, maxCount);
    }

    private static double Mean(double[] values, int start, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += values[start + i];
        }
        return sum / count;
    }

    private static double TrimmedMean(double[] values, int start, int count)
    {
        double[] tmp = new double[count];
        Array.Copy(values, start, tmp, 0, count);
        Array.Sort(tmp);

        int trim = (int)Math.Round(count * TrimFraction);
        trim = Math.Max(1, trim);
        trim = Math.Min(trim, (count - 1) / 2);

        double sum = 0;
        int used = 0;
        for (int i = trim; i < count - trim; i++)
        {
            sum += tmp[i];
            used++;
        }
        return sum / used;
    }

    private static int FindBreathStartIndex(double[] temperatureC, double baselineTemp)
    {
        for (int i = 0; i < temperatureC.Length; i++)
        {
            if (temperatureC[i] >= baselineTemp + BreathStartTempRiseC)
            {
                return i;
            }
        }
        return 0;
    }

    private static bool TryFitGasPpm(
        double[] coRaw,
        double samplePeriodSec,
        DeviceGasCalibration calibration,
        out double ppm,
        out double fitDurationSec)
    {
        ppm = 0;
        fitDurationSec = 0;
        if (coRaw.Length < 2) return false;

        int n = coRaw.Length;
        double[] times = new double[n];
        for (int i = 0; i < n; i++)
        {
            times[i] = i * samplePeriodSec;
        }

        // Same anchor strategy as app-side gas fit.
        double b0 = (coRaw[0] + coRaw[1]) / 2.0;
        double t0 = (times[0] + times[1]) / 2.0;

        double[] delta = new double[n];
        for (int i = 0; i < n; i++)
        {
            double baselineAtTime = b0 + calibration.DriftRawPerSec * (times[i] - t0);
            delta[i] = coRaw[i] - baselineAtTime;
        }

        int startIdx = FindGasStartIndex(delta, samplePeriodSec);
        double sumYF = 0;
        double sumF2 = 0;
        int usedCount = 0;
        double startSec = times[startIdx];
        for (int i = startIdx; i < n; i++)
        {
            double u = times[i] - startSec;
            if (u < 0) continue;
            if (u > FitWindowSec) break;
            double shiftedU = Math.Max(0, u - calibration.DeadTimeSec);
            double f = 1.0 - Math.Exp(-shiftedU / calibration.TauSec);
            double y = delta[i];
            sumYF += y * f;
            sumF2 += f * f;
            usedCount++;
        }
        if (sumF2 <= 0) return false;

        double a = sumYF / sumF2;
        if (double.IsNaN(a) || double.IsInfinity(a)) return false;

        ppm = a / calibration.GainRawPerPpm;
        fitDurationSec = Math.Max(0, (usedCount - 1) * samplePeriodSec);
        return !double.IsNaN(ppm) && !double.IsInfinity(ppm);
    }

    private static int FindGasStartIndex(double[] delta, double samplePeriodSec)
    {
        int n = delta.Length;
        if (n < 2) return 0;

        double[] smoothed = new double[n];
        for (int i = 0; i < n; i++)
        {
            int s = Math.Max(0, i - 1);
            int e = Math.Min(n - 1, i + 1);
            double sum = 0;
            int count = 0;
            for (int j = s; j <= e; j++)
            {
                sum += delta[j];
                count++;
            }
            smoothed[i] = sum / count;
        }

        for (int i = 1; i < n; i++)
        {
            double slope = (smoothed[i] - smoothed[i - 1]) / samplePeriodSec;
            if (slope >= StartSlopeThresholdRawPerSec && delta[i] >= StartMinDeltaRaw)
            {
                return i;
            }
        }
        return 0;
    }

    private static double QuantizeGasPpm(double ppm)
    {
        if (double.IsNaN(ppm) || double.IsInfinity(ppm))
        {
            return 0.0;
        }

        double clipped = Math.Max(0, ppm);
        // Keep strict '<' so exactly 2.5 maps to 5 ppm.
        if (clipped < GasThresholdZeroToFivePpm) return 0.0;
        if (Math.Abs(clipped - GasThresholdFiveToTenPpm) <= GasThresholdBorderlineBandPpm) return GasThresholdFiveToTenPpm;
        if (Math.Abs(clipped - GasThresholdTenToFifteenPpm) <= GasThresholdBorderlineBandPpm) return GasThresholdTenToFifteenPpm;
        if (clipped < GasThresholdFiveToTenPpm) return 5.0;
        if (clipped < GasThresholdTenToFifteenPpm) return 10.0;
        return 15.0;
    }

    private DeviceGasCalibration GetGasCalibrationForDevice(string? deviceSerial)
    {
        string? serialPrefix = NormalizeSerialPrefix(deviceSerial) ?? _deviceSerialPrefix;
        if (serialPrefix != null && DeviceGasCalibrations.TryGetValue(serialPrefix, out var calibration))
        {
            return calibration;
        }
        return DeviceGasCalibration.Global(GasGainRawPerPpm, GasTauSec);
    }

    private static string? NormalizeSerialPrefix(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }

        Span<char> buf = stackalloc char[8];
        int len = 0;
        for (int i = 0; i < serial.Length && len < 8; i++)
        {
            char c = serial[i];
            if (char.IsLetterOrDigit(c))
            {
                buf[len++] = char.ToUpperInvariant(c);
            }
        }

        return len == 8 ? new string(buf) : null;
    }

    private readonly record struct DeviceGasCalibration(
        double DriftRawPerSec,
        double GainRawPerPpm,
        double TauSec,
        double DeadTimeSec)
    {
        public static DeviceGasCalibration Global(double gainRawPerPpm, double tauSec) => new(
            DriftRawPerSec: 0.0,
            GainRawPerPpm: gainRawPerPpm,
            TauSec: tauSec,
            DeadTimeSec: 0.0);
    }

    private readonly record struct Sample(double CoRaw, double TemperatureC, double HumidityPct, double TimestampS);
}
