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

    public XHBreathResult AnalyzeBreath(double[] coRaw, double[] temperatureC, double samplePeriodSec)
    {
        if (coRaw == null || temperatureC == null) throw new ArgumentNullException("Input arrays cannot be null");
        if (coRaw.Length == 0 || temperatureC.Length == 0) return new XHBreathResult(0, 0, true, true);
        if (coRaw.Length != temperatureC.Length) throw new ArgumentException("Array lengths must match");
        if (samplePeriodSec <= 0) throw new ArgumentOutOfRangeException(nameof(samplePeriodSec));

        int n = coRaw.Length;

        // Baseline: mean of first 10% (min 3, max 50) samples
        int baselineCount = Math.Clamp((int)Math.Round(n * 0.1), 3, Math.Min(50, n));
        double baselineCo = 0;
        double baselineT = 0;
        for (int i = 0; i < baselineCount; i++)
        {
            baselineCo += coRaw[i];
            baselineT += temperatureC[i];
        }
        baselineCo /= baselineCount;
        baselineT /= baselineCount;

        // Simple smoothing with window 5
        int window = Math.Clamp((int)Math.Round(0.5 / samplePeriodSec), 3, 11); // ~0.5s
        double[] smoothed = new double[n];
        int half = window / 2;
        for (int i = 0; i < n; i++)
        {
            int start = Math.Max(0, i - half);
            int end = Math.Min(n - 1, i + half);
            double sum = 0;
            int count = 0;
            for (int j = start; j <= end; j++) { sum += coRaw[j]; count++; }
            smoothed[i] = sum / count;
        }

        // Peak search within first 6s after baseline window
        int searchStart = baselineCount;
        int searchEnd = Math.Min(n - 1, searchStart + (int)Math.Round(6.0 / samplePeriodSec));
        if (searchEnd <= searchStart) searchEnd = n - 1;
        int peakIdx = searchStart;
        double peakVal = smoothed[peakIdx];
        for (int i = searchStart + 1; i <= searchEnd; i++)
        {
            if (smoothed[i] > peakVal)
            {
                peakVal = smoothed[i];
                peakIdx = i;
            }
        }

        double duration = Math.Max(0, (peakIdx - searchStart) * samplePeriodSec);
        double meanCoRaw = 0;
        for (int i = 0; i < n; i++) meanCoRaw += coRaw[i];
        meanCoRaw /= n;
        double estimatedPpm = Math.Max(0, meanCoRaw * 1.5);

        bool shortDuration = duration < 2.0;
        double minTemp = double.PositiveInfinity, maxTemp = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            if (temperatureC[i] < minTemp) minTemp = temperatureC[i];
            if (temperatureC[i] > maxTemp) maxTemp = temperatureC[i];
        }
        bool smallTempRise = (maxTemp - minTemp) < 0.5;

        return new XHBreathResult(estimatedPpm, duration, shortDuration, smallTempRise);
    }

    public XHBreathResult AnalyzeBreath(byte[][] coRawBigEndian, double[] temperatureC, double samplePeriodSec)
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

        return AnalyzeBreath(coRaw, temperatureC, samplePeriodSec);
    }

    private readonly record struct Sample(double CoRaw, double TemperatureC, double HumidityPct, double TimestampS);
}


