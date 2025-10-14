using System;

namespace XHale.Core;

public interface IXHaleEngine
{
    void Initialize(string licenseKey);
    void ResetSession();
    XHBatteryInfo ComputeBatteryFromRawADC(double rawAdc);
    void FeedSample(double coRaw, double temperatureC, double humidityPct, DateTimeOffset timestamp);
    XHBreathResult AnalyzeBreath();
}


