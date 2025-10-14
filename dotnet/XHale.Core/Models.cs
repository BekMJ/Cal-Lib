namespace XHale.Core;

public sealed record XHBatteryInfo(int Percent, double Voltage, double CapacitymAh);
public sealed record XHBreathResult(double EstimatedPPM, double DurationSec, bool ShortDuration, bool SmallTemperatureRise);


