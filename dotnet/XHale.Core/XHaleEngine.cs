using System;
using System.Collections.Generic;
using System.Linq;

namespace XHale.Core;

public sealed class XHaleEngine : IXHaleEngine
{
    private string? _licenseKey;

    private readonly List<Sample> _samples = new(capacity: 1024);

    private const int MaxSamples = 4096;

    public void Initialize(string licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new ArgumentException("License key must be non-empty", nameof(licenseKey));
        }
        _licenseKey = licenseKey;
    }

    public void ResetSession()
    {
        _samples.Clear();
    }

    public XHBatteryInfo ComputeBatteryFromRawADC(double rawAdc)
    {
        // Stub: interpret rawAdc as a 12-bit ADC reading in [0, 4095].
        // Map to percent linearly and clamp.
        double normalized = rawAdc <= 0 ? 0 : rawAdc / 4095.0;
        if (normalized > 1) normalized = 1;
        int percent = (int)Math.Round(normalized * 100);

        // Stub voltage mapping: 3.0V at 0%, 4.2V at 100% (typical Li-ion range)
        double voltage = 3.0 + normalized * 1.2;

        // Stub capacity estimation: simple proportional mapping to 3000 mAh pack
        double capacitymAh = 3000.0 * (percent / 100.0);

        return new XHBatteryInfo(percent, voltage, capacitymAh);
    }

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
        if (_samples.Count == 0)
        {
            return new XHBreathResult(0, 0, true, true);
        }

        double start = _samples[0].TimestampS;
        double end = _samples[^1].TimestampS;
        double duration = Math.Max(0, end - start);

        // Stub estimated PPM: mean of coRaw scaled to a plausible range
        double meanCoRaw = _samples.Average(s => s.CoRaw);
        double estimatedPpm = Math.Max(0, meanCoRaw * 1.5);

        // Stub thresholds
        bool shortDuration = duration < 2.0;

        double minTemp = _samples.Min(s => s.TemperatureC);
        double maxTemp = _samples.Max(s => s.TemperatureC);
        bool smallTempRise = (maxTemp - minTemp) < 0.5;

        return new XHBreathResult(estimatedPpm, duration, shortDuration, smallTempRise);
    }

    private readonly record struct Sample(double CoRaw, double TemperatureC, double HumidityPct, double TimestampS);
}


