using System;

namespace XHale.Core;

public interface IXHaleEngine
{
    void Initialize(string licenseKey);
    void ResetSession();
    void FeedSample(double coRaw, double temperatureC, double humidityPct, DateTimeOffset timestamp);
    XHBreathResult AnalyzeBreath();

    // Batch analysis: arrays of raw CO and temperature with a fixed sample period (seconds)
    XHBreathResult AnalyzeBreath(double[] coRaw, double[] temperatureC, double samplePeriodSec);

    // Batch analysis: arrays of big-endian CO bytes (each sample is 2 bytes: MSB, LSB)
    XHBreathResult AnalyzeBreath(byte[][] coRawBigEndian, double[] temperatureC, double samplePeriodSec);
}


