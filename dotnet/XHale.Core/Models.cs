namespace XHale.Core;

public sealed record XHBreathResult(double EstimatedPPM, double DurationSec, bool ShortDuration, bool SmallTemperatureRise);

public sealed record XHDeviceGasCalibration(
    double DriftRawPerSec,
    double GainRawPerPpm,
    double TauSec,
    double DeadTimeSec
);

