using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace XHale.Core;

public sealed class XHaleEngine : IXHaleEngine
{
    private string? _licenseKey;
    private string? _deviceSerialPrefix;
    private string? _modelNumber;
    // True when the host reported that reading DIS Model Number (0x2A24) failed (not "absent").
    private bool _modelNumberLookupFailed;
    private DeviceGasCalibration? _firmwareDeviceGasCalibration;
    private readonly Dictionary<string, DeviceGasCalibration> _injectedDeviceGasCalibrations = new(StringComparer.Ordinal);

    private readonly List<Sample> _samples = new(capacity: 1024);

    private const int MaxSamples = 4096;
    // Calibration (current):
    // 1) Breath start (temp): first temp sample >= initial temp baseline + 0.8°C.
    // 2) r_base_pre: trimmed mean of pre-breath CO if >=5 samples; else warmup baseline; else first CO sample.
    // 3) T_base_pre: average of pre-breath temps; else initial temp baseline.
    // 4) r_peak (human path): max raw CO after breath start (no smoothing).
    // 5) ΔT = T_peak - T_base_pre.
    // 6) Decision rule: if ΔT > 2.0°C use human-breath calibration; else gas-fit calibration.
    // 7) Human path: ppm = max(0, (ΔR_comp - intercept) / slope), using the paired-reference refit.
    // 8) Gas path: firmware CAL_READ calibration wins; otherwise known serials use per-device exponential fit;
    //    unknown serials use a median generic gas profile.
    // 9) Gas output is discretized to 0/5/10/15 ppm only inside the configured display bands.
    // 10) Quality gates: ShortDuration (<5s), SmallTemperatureRise (ΔT < 1°C).
    private const double CalibrationGasFallbackSlopeRawPerPpm = 0.98;
    private const double CalibrationGasFallbackInterceptRaw = -1.8;
    private const double BreathCalibrationSlopeRawPerPpm = 11.558260683760434;
    private const double BreathCalibrationInterceptRaw = 63.20735653235655;
    private const double BreathCalibrationTempThresholdC = 2.0;
    private const double BreathStartTempRiseC = 0.8;
    private const double TemperatureCompensationRawPerC = 17.30;
    private const double VoltageCompensationRawPerV = 150.30;
    private const double WarmupDurationSec = 20.0;
    private const double FitWindowSec = 20.0;
    private const double StartSlopeThresholdRawPerSec = 0.1;
    private const double StartMinDeltaRaw = 1.0;
    private const double TrimFraction = 0.10;
    private const int FirmwareCalibrationRecordSize = 32;
    private const uint FirmwareCalibrationMagic = 0x5843414C; // "XCAL" as little-endian uint32.
    private const byte FirmwareCalibrationVersion = 1;
    private const byte FirmwareCalibrationValidFlag = 0x01;
    private const int FirmwareCalibrationOffsetMagic = 0;
    private const int FirmwareCalibrationOffsetVersion = 4;
    private const int FirmwareCalibrationOffsetFlags = 5;
    private const int FirmwareCalibrationOffsetGainRawPerPpm = 12;
    private const int FirmwareCalibrationOffsetTauSec = 16;
    private const int FirmwareCalibrationOffsetDeadSec = 20;
    private const int FirmwareCalibrationOffsetDriftRawPerSec = 24;
    private const int FirmwareCalibrationOffsetCrc16 = 30;
    // Robust median of the bundled per-device gas profiles. Used only when no firmware,
    // injected, or serial-table calibration is available.
    private static readonly DeviceGasCalibration GenericDeviceGasCalibration = new(
        -6.100806002899952,
        26.115734843177872,
        1.669570638561888,
        5.0);
    // Board-specific SGX fallback when an SGX device has no firmware, injected, or bundled calibration.
    private static DeviceGasCalibration SgxFallbackGasCalibration(XHSensorProfile profile) => new(
        0.0,
        XHSensorProfiles.SgxFallbackGainRawPerPpm(profile) ?? XHSensorProfiles.NrfSgxFallbackGainRawPerPpm,
        1.0,
        0.0);
    private static readonly Dictionary<string, DeviceGasCalibration> DefaultDeviceGasCalibrations = new(StringComparer.Ordinal)
    {
        ["089D110A"] = new DeviceGasCalibration(-6.201368523949169, 25.272945436392902, 0.5, 6.0),
        ["2924BA99"] = new DeviceGasCalibration(-5.598589065255732, 25.03119401510033, 1.5, 5.0),
        ["36F14E25"] = new DeviceGasCalibration(-11.614786086534453, 41.716293614542884, 1.0, 4.4999999999999964),
        ["3BBF9785"] = new DeviceGasCalibration(-7.030079043080153, 26.809030715544022, 0.5, 2.999999996791774),
        ["4E6D1A4D"] = new DeviceGasCalibration(-5.248677248677249, 25.132835447724382, 2.0, 0.0),
        ["4F2F6B63"] = new DeviceGasCalibration(-12.012171684817618, 22.527506773281576, 1.0, 0.0),
        ["525D442D"] = new DeviceGasCalibration(-7.7360368663594485, 26.93024617146331, 0.5, 6.0),
        ["6C8A4BC7"] = new DeviceGasCalibration(2.079120754421729, 23.656395073833398, 0.6222089674828004, 8.0),
        ["6F19F9D0"] = new DeviceGasCalibration(-5.439218510632482, 28.836833289278694, 1.8391412771237763, 6.438117004026197),
        ["794093CF"] = new DeviceGasCalibration(-0.6666643338872953, 24.844493808756724, 0.6297863426404107, 6.042329485419282),
        ["7ED2FA3A"] = new DeviceGasCalibration(-1.9995184049332049, 78.55491344311771, 12.0, 8.0),
        ["7FF4CB9D"] = new DeviceGasCalibration(-10.0332133813142, 35.121022728144894, 1.0, 3.4999999999999964),
        ["8B817936"] = new DeviceGasCalibration(-4.798790627362056, 25.422438970811722, 0.5, 3.0),
        ["96ABD9E6"] = new DeviceGasCalibration(-6.506666666666668, 24.513387824065155, 5.0, 5.0),
        ["9C04C961"] = new DeviceGasCalibration(-6.619501133786848, 23.47937533700087, 2.0, 2.0),
        ["9E9F6459"] = new DeviceGasCalibration(-10.141800128122737, 62.29243020944069, 9.0, 6.9999999999999964),
        ["A73EB860"] = new DeviceGasCalibration(-7.250830889540567, 24.70269801110892, 0.5, 5.0),
        ["B73545B1"] = new DeviceGasCalibration(-10.894644909215634, 222.95554759553872, 37.0, 6.9999999999999964),
        ["CAE82C50"] = new DeviceGasCalibration(-6.708089330024814, 24.31855758437466, 3.0, 6.0),
        ["F37B9134"] = new DeviceGasCalibration(-7.635978835978836, 38.230704909693806, 3.0, 6.0),
        ["D1A07CD4"] = new DeviceGasCalibration(-1.4545457480006594, 68.37467929189923, 11.576681630123998, 7.35659477241304),
        ["D92EC0CB"] = new DeviceGasCalibration(0.00006792920907690491, 21.210898372231238, 0.5, 0.0),
        ["DC3F9A7B"] = new DeviceGasCalibration(1.4605736066292012, 37.84666292119706, 3.7579959691515286, 6.254430626213045),
        ["E0AED989"] = new DeviceGasCalibration(-3.1222597553873244, 61.93527632478544, 22.0, 3.0),
        ["E5ACF73C"] = new DeviceGasCalibration(-10.882020525122973, 35.64978304202127, 1.0, 0.0),
        ["F2E4CB88"] = new DeviceGasCalibration(-1.7976168208247028, 23.94750224508536, 6.146536035184152, 1.2090464430028565),
        ["F685F16F"] = new DeviceGasCalibration(-6.000243481850735, 75.70296570995819, 12.0, 7.7959056072879145),
        ["F7CF3358"] = new DeviceGasCalibration(-2.887169760194338, 18.93036200071086, 1.0, 0.0),
        // nRF + SGX prototypes (gain-only fit; algorithm settings drift 0, tau 1 s, dead 0).
        // 0DC44112 is also an SGX prototype but has no bundled gain: it needs firmware or injected constants.
        ["A793BAFC"] = new DeviceGasCalibration(0.0, 327.436077, 1.0, 0.0),
        ["D0F18121"] = new DeviceGasCalibration(0.0, 307.666794, 1.0, 0.0),
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
        string? prefix = NormalizeSerialPrefix(serial);
        if (!string.Equals(prefix, _deviceSerialPrefix, StringComparison.Ordinal))
        {
            // Switching away from a known device (to another device, or to null on disconnect) forgets
            // that device's Model Number. When no device was set yet, a Model Number supplied first
            // belongs to the incoming device and is kept, so BLE read order does not matter.
            if (_deviceSerialPrefix != null)
            {
                _modelNumber = null;
                _modelNumberLookupFailed = false;
                // Firmware CAL_READ constants belong to the device they were read from.
                _firmwareDeviceGasCalibration = null;
            }
        }
        _deviceSerialPrefix = prefix;
    }

    public void SetDeviceIdentity(string? serial, string? modelNumber)
        => SetDeviceIdentity(serial, modelNumber, modelNumberLookupFailed: false);

    /// <summary>
    /// Sets the connected device's identity. Pass <paramref name="modelNumber"/> = null only when the device has
    /// no Model Number characteristic (legacy Winsen firmware). If 0x2A24 exists but its read failed, pass
    /// <paramref name="modelNumberLookupFailed"/> = true: the SDK then blocks prediction instead of assuming Winsen
    /// (known SGX prototype serials still resolve to SGX).
    /// </summary>
    public void SetDeviceIdentity(string? serial, string? modelNumber, bool modelNumberLookupFailed)
    {
        string? prefix = NormalizeSerialPrefix(serial);
        // Switching away from a known device drops its firmware calibration. When no device was set yet,
        // a firmware calibration supplied first belongs to the incoming device and is kept.
        if (_deviceSerialPrefix != null && !string.Equals(prefix, _deviceSerialPrefix, StringComparison.Ordinal))
        {
            _firmwareDeviceGasCalibration = null;
        }
        _deviceSerialPrefix = prefix;
        _modelNumber = XHSensorProfiles.NormalizeModelNumber(modelNumber);
        _modelNumberLookupFailed = _modelNumber == null && modelNumberLookupFailed;
    }

    /// <summary>Call on disconnect. Forgets serial, Model Number and the device's firmware calibration.</summary>
    public void ClearDeviceIdentity()
    {
        _deviceSerialPrefix = null;
        _modelNumber = null;
        _modelNumberLookupFailed = false;
        _firmwareDeviceGasCalibration = null;
    }

    public void SetModelNumber(string? modelNumber)
    {
        _modelNumber = XHSensorProfiles.NormalizeModelNumber(modelNumber);
        if (_modelNumber != null) _modelNumberLookupFailed = false;
    }

    /// <summary>
    /// Reports that the device's Model Number (0x2A24) could not be read. Do not call this when the characteristic
    /// is simply absent (legacy Winsen); in that case leave the Model Number null.
    /// </summary>
    public void SetModelNumberLookupFailed()
    {
        _modelNumber = null;
        _modelNumberLookupFailed = true;
    }

    public XHSensorProfileResolution GetSensorProfile(string? deviceSerial = null)
    {
        return XHSensorProfiles.Resolve(_modelNumber, NormalizeSerialPrefix(deviceSerial) ?? _deviceSerialPrefix, _modelNumberLookupFailed);
    }

    public bool TrySetFirmwareGasCalibrationRecord(byte[] calibrationRecord)
    {
        if (!TryParseFirmwareGasCalibrationRecord(calibrationRecord, out var calibration))
        {
            return false;
        }

        SetFirmwareGasCalibration(calibration);
        return true;
    }

    public bool TryParseFirmwareGasCalibrationRecord(byte[] calibrationRecord, out XHDeviceGasCalibration calibration)
    {
        calibration = default!;
        if (calibrationRecord == null || calibrationRecord.Length != FirmwareCalibrationRecordSize)
        {
            return false;
        }

        ReadOnlySpan<byte> record = calibrationRecord;
        if (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(FirmwareCalibrationOffsetMagic, sizeof(uint))) != FirmwareCalibrationMagic)
        {
            return false;
        }
        if (record[FirmwareCalibrationOffsetVersion] != FirmwareCalibrationVersion)
        {
            return false;
        }
        byte flags = record[FirmwareCalibrationOffsetFlags];
        if ((flags & ~FirmwareCalibrationValidFlag) != 0 || (flags & FirmwareCalibrationValidFlag) == 0)
        {
            return false;
        }

        ushort storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(FirmwareCalibrationOffsetCrc16, sizeof(ushort)));
        ushort computedCrc = ComputeFirmwareCrc16(record.Slice(0, FirmwareCalibrationOffsetCrc16));
        if (storedCrc != computedCrc)
        {
            return false;
        }

        double gainRawPerPpm = ReadSingleLittleEndian(record, FirmwareCalibrationOffsetGainRawPerPpm);
        double tauSec = ReadSingleLittleEndian(record, FirmwareCalibrationOffsetTauSec);
        double deadSec = ReadSingleLittleEndian(record, FirmwareCalibrationOffsetDeadSec);
        double driftRawPerSec = ReadSingleLittleEndian(record, FirmwareCalibrationOffsetDriftRawPerSec);
        if (!IsValidFirmwareCalibration(driftRawPerSec, gainRawPerPpm, tauSec, deadSec))
        {
            return false;
        }

        calibration = new XHDeviceGasCalibration(driftRawPerSec, gainRawPerPpm, tauSec, deadSec);
        return true;
    }

    public void SetFirmwareGasCalibration(XHDeviceGasCalibration calibration)
    {
        if (calibration == null) throw new ArgumentNullException(nameof(calibration));
        if (!IsValidFirmwareCalibration(calibration.DriftRawPerSec, calibration.GainRawPerPpm, calibration.TauSec, calibration.DeadTimeSec))
        {
            throw new ArgumentOutOfRangeException(nameof(calibration), "Firmware gas calibration values are outside the accepted range.");
        }

        _firmwareDeviceGasCalibration = new DeviceGasCalibration(
            calibration.DriftRawPerSec,
            calibration.GainRawPerPpm,
            calibration.TauSec,
            Math.Max(0.0, calibration.DeadTimeSec)
        );
    }

    public void ClearFirmwareGasCalibration()
    {
        _firmwareDeviceGasCalibration = null;
    }

    public void SetPerDeviceGasCalibration(string? serialOrPrefix, double driftRawPerSec, double gainRawPerPpm, double tauSec, double deadTimeSec)
    {
        string? prefix = NormalizeSerialPrefix(serialOrPrefix);
        if (prefix == null)
        {
            throw new ArgumentException("Serial or prefix must normalize to 8 alphanumeric characters.", nameof(serialOrPrefix));
        }
        if (gainRawPerPpm <= 0) throw new ArgumentOutOfRangeException(nameof(gainRawPerPpm));
        if (tauSec <= 0) throw new ArgumentOutOfRangeException(nameof(tauSec));

        _injectedDeviceGasCalibrations[prefix] = new DeviceGasCalibration(
            driftRawPerSec,
            gainRawPerPpm,
            tauSec,
            Math.Max(0.0, deadTimeSec)
        );
    }

    public void ClearPerDeviceGasCalibration(string? serialOrPrefix)
    {
        string? prefix = NormalizeSerialPrefix(serialOrPrefix);
        if (prefix != null)
        {
            _injectedDeviceGasCalibrations.Remove(prefix);
        }
    }

    public void ClearAllInjectedPerDeviceGasCalibrations()
    {
        _injectedDeviceGasCalibrations.Clear();
    }

    public void ResetSession()
    {
        _samples.Clear();
    }

    // Battery computation removed per request

    public void FeedSample(double coRaw, double temperatureC, double humidityPct, DateTimeOffset timestamp)
    {
        // Millisecond precision: whole seconds would collapse sub-second sample spacing.
        double ts = timestamp.ToUnixTimeMilliseconds() / 1000.0;
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

        // Select the processing profile before any calculation (mirrors iOS SensorProfile.swift).
        XHSensorProfileResolution profileResolution = GetSensorProfile(deviceSerial);
        if (profileResolution.Profile is not { } sensorProfile)
        {
            return BlockedResult(profileResolution, _modelNumberLookupFailed && profileResolution.ModelNumber == null
                ? "Could not identify this device model, so no CO value is produced. Reconnect the device and try again."
                : $"This device is not supported by this SDK version: {profileResolution.UnsupportedReason}. Please update the SDK.");
        }
        if (sensorProfile == XHSensorProfile.EfrSgx && !XHSensorProfiles.EfrSgxPredictionEnabled)
        {
            return BlockedResult(profileResolution,
                "EFR + SGX devices are recognized, but CO prediction is not enabled yet in this SDK version.");
        }
        bool isLegacyWinsen = XHSensorProfiles.AllowsLegacyHumanBreathBranch(sensorProfile);

        // Two input conventions:
        //  - Sample-only (a baseline was set with SetBaseline*/SetBaselineFromBytes after warmup, and the arrays hold
        //    only the breath sample): identical to the iOS app's analyzeCurrentBreath().
        //  - Legacy (no baseline set): the first 20 s of the arrays are treated as warmup.
        bool sampleOnly = _baselineExplicitCoRaw.HasValue;
        int breathStartIdx;
        double tBasePre;
        double rBasePre;
        if (sampleOnly)
        {
            // App: breath start = first temperature >= mean(first 10 temps) + 0.8 C, else the sample start.
            double t0Initial = Mean(temperatureC, 0, Math.Min(10, n));
            breathStartIdx = FindBreathStartIndex(temperatureC, t0Initial);
            int preCount = breathStartIdx;
            tBasePre = preCount >= 5 ? Mean(temperatureC, 0, preCount) : t0Initial;
            if (preCount >= 5)
            {
                rBasePre = AppTrimmedMean(coRaw, 0, preCount);
            }
            else if (preCount >= 2)
            {
                rBasePre = Mean(coRaw, 0, 2);
            }
            else
            {
                // Fewer than 2 pre-breath samples: the baseline captured at the end of warmup.
                rBasePre = _baselineExplicitCoRaw!.Value;
            }
        }
        else
        {
            int warmupSampleCount = ComputeSampleCountForSeconds(WarmupDurationSec, samplePeriodSec, n);
            double warmupCoBaseline = Mean(coRaw, 0, warmupSampleCount);
            double warmupTempBaseline = Mean(temperatureC, 0, warmupSampleCount);

            breathStartIdx = FindBreathStartIndex(temperatureC, warmupTempBaseline);
            tBasePre = breathStartIdx > 0
                ? Mean(temperatureC, 0, breathStartIdx)
                : warmupTempBaseline;

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

        // App: breath duration runs from breath start to the last sample; legacy: breath start to peak.
        double duration = sampleOnly
            ? Math.Max(0, (n - 1 - breathStartIdx) * samplePeriodSec)
            : Math.Max(0, (peakIdx - breathStartIdx) * samplePeriodSec);
        // App keys the duration-bucket fallback by the configured sample length.
        double fallbackEvalSeconds = sampleOnly ? n * samplePeriodSec : duration;
        double rPeak = peakVal;
        double tPeak = temperatureC[peakIdx];
        double deltaT = tPeak - tBasePre;

        // Temperature-triggered human-breath formula is Winsen-only; SGX always uses its gas calibration.
        bool useBreathCalibration = isLegacyWinsen && deltaT > BreathCalibrationTempThresholdC;
        double estimatedPpm;
        bool usedGenericGasCalibration = false;
        bool usedSgxFallbackCalibration = false;
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
            bool allowBundledTable = XHSensorProfiles.AllowsBundledNrfCalibrationTable(sensorProfile);
            if (TryGetGasCalibrationForDevice(deviceSerial, allowBundledTable, out var calibration))
            {
                usedGenericGasCalibration = calibration == GenericDeviceGasCalibration;
                if (!XHSensorProfiles.AllowsWinsenFallbackConstants(sensorProfile)
                    && !HasDeviceSpecificGasCalibration(deviceSerial, allowBundledTable))
                {
                    if (!XHSensorProfiles.AllowsSgxFallbackCalibration(sensorProfile))
                    {
                        return BlockedResult(profileResolution,
                            $"{XHSensorProfiles.DisplayName(sensorProfile)} device has no calibration (firmware, injected, or bundled). Calibrate it before measuring.")
                            with { MissingGasCalibration = true };
                    }
                    // SGX without its own calibration: use that board's SGX fallback gain, never the Winsen generic profile.
                    calibration = SgxFallbackGasCalibration(sensorProfile);
                    usedGenericGasCalibration = false;
                    usedSgxFallbackCalibration = true;
                }
                if (TryFitGasPpm(coRaw, samplePeriodSec, calibration, out double gasPpm, out double fitDuration))
                {
                    if (!sampleOnly) duration = fitDuration;
                    // The 0/5/10/15 ppm bucketing is Winsen-only; SGX reports the continuous gas-fit ppm.
                    estimatedPpm = isLegacyWinsen ? QuantizeGasPpm(Math.Max(0, gasPpm)) : Math.Max(0, gasPpm);
                }
                else if (!XHSensorProfiles.AllowsWinsenFallbackConstants(sensorProfile))
                {
                    // Legacy free-fit coefficients are Winsen-specific; never apply them to SGX.
                    return BlockedResult(profileResolution,
                        $"Could not fit the CO response for this {XHSensorProfiles.DisplayName(sensorProfile)} sample. Please repeat the measurement.");
                }
                else
                {
                    // Match the app-side duration-bucket fallback when a known-device gas fit is not computable.
                    double deltaV = 0.0;
                    double deltaRComp = (rPeak - rBasePre)
                        - (TemperatureCompensationRawPerC * deltaT)
                        - (VoltageCompensationRawPerV * deltaV);
                    var gasCoefficients = GasCalibrationCoefficients(fallbackEvalSeconds);
                    double fallbackPpm = Math.Max(0, (deltaRComp - gasCoefficients.Intercept) / gasCoefficients.Slope);
                    estimatedPpm = QuantizeGasPpm(fallbackPpm);
                }
            }
            else
            {
                throw new InvalidOperationException("Gas calibration fallback was not available.");
            }
        }

        bool shortDuration = duration < 5.0;
        bool smallTempRise = deltaT < 1.0;

        return new XHBreathResult(estimatedPpm, duration, shortDuration, smallTempRise)
        {
            UsedGenericGasCalibration = usedGenericGasCalibration,
            UsedSgxFallbackCalibration = usedSgxFallbackCalibration,
            SensorProfile = XHSensorProfiles.Tag(sensorProfile),
            SensorProfileSource = profileResolution.Source,
        };
    }

    private static XHBreathResult BlockedResult(XHSensorProfileResolution resolution, string reason)
    {
        // Quality flags stay false: the reason is PredictionBlocked/PredictionBlockedReason, not the breath.
        return new XHBreathResult(0, 0, false, false)
        {
            SensorProfile = resolution.Profile is { } p ? XHSensorProfiles.Tag(p) : "unsupported",
            SensorProfileSource = resolution.Source,
            PredictionBlocked = true,
            PredictionBlockedReason = reason,
        };
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

        // Profile gating: SGX never uses the Winsen human-breath branch or Winsen fallback constants.
        XHSensorProfileResolution profileResolution = GetSensorProfile();
        if (profileResolution.Profile is not { } sensorProfile)
        {
            throw new InvalidOperationException($"Device not supported by this SDK version: {profileResolution.UnsupportedReason}.");
        }
        if (sensorProfile == XHSensorProfile.EfrSgx && !XHSensorProfiles.EfrSgxPredictionEnabled)
        {
            throw new InvalidOperationException("EFR + SGX CO prediction is not enabled yet in this SDK version.");
        }
        bool isLegacyWinsen = XHSensorProfiles.AllowsLegacyHumanBreathBranch(sensorProfile);
        bool allowBundledTable = XHSensorProfiles.AllowsBundledNrfCalibrationTable(sensorProfile);
        bool useSgxFallback = !isLegacyWinsen && !HasDeviceSpecificGasCalibration(deviceSerial: null, allowBundledTable);
        if (useSgxFallback && !XHSensorProfiles.AllowsSgxFallbackCalibration(sensorProfile))
        {
            throw new InvalidOperationException($"{XHSensorProfiles.DisplayName(sensorProfile)} device has no calibration (firmware, injected, or bundled).");
        }

        uint coRaw = DecodeCoRawFromBytes(coBytes, bigEndian);
        double tempC = DecodeTemperatureC(temperatureBytes);

        double deltaR = coRaw - _baselineExplicitCoRaw.Value;
        double deltaT = tempC - _baselineExplicitTempC.Value;
        double compensatedDeltaR = deltaR - (TemperatureCompensationRawPerC * deltaT);

        bool useBreathCalibration = isLegacyWinsen && deltaT > BreathCalibrationTempThresholdC;
        if (!useBreathCalibration)
        {
            if (useSgxFallback)
            {
                return Math.Max(0.0, deltaR / SgxFallbackGasCalibration(sensorProfile).GainRawPerPpm);
            }
            if (!TryGetGasCalibrationForDevice(deviceSerial: null, allowBundledTable, out var calibration))
            {
                return 0.0;
            }
            return Math.Max(0.0, deltaR / calibration.GainRawPerPpm);
        }

        return Math.Max(0, (compensatedDeltaR - BreathCalibrationInterceptRaw) / BreathCalibrationSlopeRawPerPpm);
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

    // Same as the iOS app's trimmedMeanOf: drop floor(n * 10%) values at each end (possibly none).
    private static double AppTrimmedMean(double[] values, int start, int count)
    {
        double[] tmp = new double[count];
        Array.Copy(values, start, tmp, 0, count);
        Array.Sort(tmp);
        int k = Math.Max(0, Math.Min(count / 2, (int)(count * TrimFraction)));
        int used = count - 2 * k;
        if (used <= 0) return tmp[count / 2];
        double sum = 0;
        for (int i = k; i < count - k; i++) sum += tmp[i];
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
        if (clipped == 0.0) return 0.0;
        if (clipped >= 2.0 && clipped <= 7.0) return 5.0;
        if (clipped >= 8.0 && clipped <= 12.0) return 10.0;
        if (clipped >= 13.0 && clipped <= 17.0) return 15.0;
        return clipped;
    }

    private static (double Slope, double Intercept) GasCalibrationCoefficients(double evalSeconds)
    {
        int evalKey = (int)Math.Round(evalSeconds);
        return evalKey switch
        {
            5 => (0.0406375, -0.0770252),
            10 => (0.126693, -0.475432),
            15 => (0.176892, -0.0411687),
            20 => (0.241434, -0.339973),
            30 => (0.305976, -0.638778),
            40 => (0.349004, -0.837981),
            50 => (0.370518, -0.937583),
            60 => (0.370518, -0.937583),
            _ => (CalibrationGasFallbackSlopeRawPerPpm, CalibrationGasFallbackInterceptRaw),
        };
    }

    // Firmware CAL_READ constants describe the currently identified device only. A call that names a
    // different device explicitly must not pick them up.
    private bool FirmwareCalibrationAppliesTo(string? deviceSerial)
    {
        if (!_firmwareDeviceGasCalibration.HasValue) return false;
        string? requested = NormalizeSerialPrefix(deviceSerial);
        return requested == null
            || _deviceSerialPrefix == null
            || string.Equals(requested, _deviceSerialPrefix, StringComparison.Ordinal);
    }

    // allowBundledTable is false for EFR + SGX: the bundled table holds nRF-board fits only.
    private bool TryGetGasCalibrationForDevice(string? deviceSerial, bool allowBundledTable, out DeviceGasCalibration calibration)
    {
        if (FirmwareCalibrationAppliesTo(deviceSerial) && _firmwareDeviceGasCalibration is { } firmwareCalibration)
        {
            calibration = firmwareCalibration;
            return true;
        }

        string? serialPrefix = NormalizeSerialPrefix(deviceSerial) ?? _deviceSerialPrefix;
        if (serialPrefix != null && _injectedDeviceGasCalibrations.TryGetValue(serialPrefix, out var injectedCalibration))
        {
            calibration = injectedCalibration;
            return true;
        }
        if (allowBundledTable && serialPrefix != null && DefaultDeviceGasCalibrations.TryGetValue(serialPrefix, out var bundledCalibration))
        {
            calibration = bundledCalibration;
            return true;
        }
        calibration = GenericDeviceGasCalibration;
        return true;
    }

    // True when firmware, injected, or bundled per-device constants exist (i.e. not the generic Winsen fallback).
    private bool HasDeviceSpecificGasCalibration(string? deviceSerial, bool allowBundledTable)
    {
        if (FirmwareCalibrationAppliesTo(deviceSerial)) return true;
        string? serialPrefix = NormalizeSerialPrefix(deviceSerial) ?? _deviceSerialPrefix;
        if (serialPrefix == null) return false;
        return _injectedDeviceGasCalibrations.ContainsKey(serialPrefix)
            || (allowBundledTable && DefaultDeviceGasCalibrations.ContainsKey(serialPrefix));
    }

    private static double ReadSingleLittleEndian(ReadOnlySpan<byte> record, int offset)
    {
        int bits = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(offset, sizeof(float)));
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static ushort ComputeFirmwareCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc = (ushort)(((crc >> 8) & 0xFF) | (crc << 8));
            crc ^= b;
            crc ^= (ushort)(((byte)(crc & 0xFF)) >> 4);
            crc ^= (ushort)((crc << 8) << 4);
            crc ^= (ushort)(((crc & 0xFF) << 4) << 1);
        }
        return crc;
    }

    private static bool IsValidFirmwareCalibration(double driftRawPerSec, double gainRawPerPpm, double tauSec, double deadTimeSec)
    {
        return IsFinite(gainRawPerPpm) && gainRawPerPpm >= 0.001 && gainRawPerPpm <= 100000.0
            && IsFinite(tauSec) && tauSec >= 0.0 && tauSec <= 3600.0
            && IsFinite(deadTimeSec) && deadTimeSec >= 0.0 && deadTimeSec <= 600.0
            && IsFinite(driftRawPerSec) && driftRawPerSec >= -1000.0 && driftRawPerSec <= 1000.0;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
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
    }

    private readonly record struct Sample(double CoRaw, double TemperatureC, double HumidityPct, double TimestampS);
}
