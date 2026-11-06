namespace XHale.Core;

public sealed record XHBreathResult(double EstimatedPPM, double DurationSec, bool ShortDuration, bool SmallTemperatureRise)
{
    public bool MissingGasCalibration { get; init; }
    public bool UsedGenericGasCalibration { get; init; }
    /// <summary>True when an nRF + SGX device had no device calibration and the SGX fallback gain was used.</summary>
    public bool UsedSgxFallbackCalibration { get; init; }
    /// <summary>Profile tag used for this analysis: "nrf_me3", "nrf_sgx", "efr_sgx", or "unsupported".</summary>
    public string SensorProfile { get; init; } = "unknown";
    /// <summary>Why that profile was chosen: "model_number", "known_sgx_serial", "legacy_no_model_number" or "unsupported".</summary>
    public string SensorProfileSource { get; init; } = "unknown";
    /// <summary>
    /// True when no ppm could be produced for this device (unsupported Model Number, EFR + SGX not yet enabled,
    /// SGX device without calibration, or SGX gas fit failed). <see cref="EstimatedPPM"/> is 0 and must NOT be shown as a reading.
    /// </summary>
    public bool PredictionBlocked { get; init; }
    /// <summary>Human-readable reason when <see cref="PredictionBlocked"/> is true.</summary>
    public string? PredictionBlockedReason { get; init; }
}

public sealed record XHDeviceGasCalibration(
    double DriftRawPerSec,
    double GainRawPerPpm,
    double TauSec,
    double DeadTimeSec
);
