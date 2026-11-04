using System;
using System.Collections.Generic;

namespace XHale.Core;

/// <summary>
/// Sensor/board processing profile. Mirrors the iOS app's SensorProfile.swift.
/// </summary>
/// <remarks>
/// Identification contract (firmware -> host app -> SDK):
/// Device Information Service 0x180A, Model Number String 0x2A24, UTF-8, no trailing NUL:
///   "XHale-NRF-ME3"  nRF + Winsen ME3 (only on newly built Winsen firmware; optional)
///   "XHale-NRF-SGX"  nRF + SGX
///   "XHale-EFR-SGX"  EFR + SGX
/// Firmware Revision 0x2A26 is a software version and is not used for profile selection.
/// </remarks>
public enum XHSensorProfile
{
    /// <summary>nRF + Winsen ME3. Legacy behavior, including the temperature-triggered human-breath formula.</summary>
    NrfMe3,
    /// <summary>nRF + SGX. Per-device gas fit at every temperature rise; no Winsen human-breath formula or fallback constants.</summary>
    NrfSgx,
    /// <summary>EFR + SGX. Recognized, but prediction is disabled until its raw format and per-device calibrations are confirmed.</summary>
    EfrSgx,
}

/// <summary>Result of resolving which processing profile applies to the current device.</summary>
/// <param name="Profile">The selected profile, or null when the device is not supported by this SDK version.</param>
/// <param name="Source">Why: "model_number", "known_sgx_serial", "legacy_no_model_number" or "unsupported".</param>
/// <param name="ModelNumber">The normalized Model Number the decision was based on, or null if none was provided.</param>
/// <param name="UnsupportedReason">Set when <paramref name="Profile"/> is null.</param>
public sealed record XHSensorProfileResolution(
    XHSensorProfile? Profile,
    string Source,
    string? ModelNumber,
    string? UnsupportedReason)
{
    public bool IsSupported => Profile.HasValue;
}

/// <summary>Pure profile-resolution rules shared by the engine. Same rules as the iOS app.</summary>
public static class XHSensorProfiles
{
    public const string ModelNumberNrfMe3 = "XHale-NRF-ME3";
    public const string ModelNumberNrfSgx = "XHale-NRF-SGX";
    public const string ModelNumberEfrSgx = "XHale-EFR-SGX";

    /// <summary>
    /// SGX prototypes built before Model Number support existed. They report no Model Number,
    /// so without this list they would be treated as Winsen.
    /// </summary>
    public static IReadOnlyCollection<string> KnownLegacySgxSerialPrefixes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "A793BAFC", "D0F18121", "0DC44112" };

    /// <summary>
    /// EFR + SGX is identified but not yet allowed to produce ppm. Enable only after the EFR raw CO
    /// encoding/scaling is confirmed and EFR devices have their own calibrations (nRF-SGX gains must not be reused).
    /// </summary>
    public const bool EfrSgxPredictionEnabled = false;

    public static string Tag(XHSensorProfile profile) => profile switch
    {
        XHSensorProfile.NrfMe3 => "nrf_me3",
        XHSensorProfile.NrfSgx => "nrf_sgx",
        XHSensorProfile.EfrSgx => "efr_sgx",
        _ => "unknown",
    };

    public static string DisplayName(XHSensorProfile profile) => profile switch
    {
        XHSensorProfile.NrfMe3 => "nRF + Winsen ME3",
        XHSensorProfile.NrfSgx => "nRF + SGX",
        XHSensorProfile.EfrSgx => "EFR + SGX",
        _ => "Unknown",
    };

    /// <summary>Only the legacy Winsen profile may use the temperature-triggered human-breath formula and Winsen compensation.</summary>
    public static bool AllowsLegacyHumanBreathBranch(XHSensorProfile profile) => profile == XHSensorProfile.NrfMe3;

    /// <summary>Only the legacy Winsen profile may fall back to Winsen generic constants or the legacy free-fit coefficients.</summary>
    public static bool AllowsWinsenFallbackConstants(XHSensorProfile profile) => profile == XHSensorProfile.NrfMe3;

    /// <summary>Strips NUL terminators and surrounding whitespace; returns null when nothing is left.</summary>
    public static string? NormalizeModelNumber(string? raw)
    {
        if (raw == null) return null;
        string cleaned = raw.Replace("\0", string.Empty).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Resolves the processing profile.
    /// 1. Recognized Model Number -> that profile.
    /// 2. Unrecognized Model Number -> unsupported.
    /// 3. No Model Number -> legacy nRF + Winsen ME3, except known SGX prototype serials.
    /// </summary>
    /// <param name="modelNumber">Value read from 0x2A24, or null if the device has no Model Number.</param>
    /// <param name="serialPrefix8">Normalized 8-character serial prefix, if known.</param>
    public static XHSensorProfileResolution Resolve(string? modelNumber, string? serialPrefix8)
    {
        string? model = NormalizeModelNumber(modelNumber);
        if (model != null)
        {
            XHSensorProfile? profile = model switch
            {
                ModelNumberNrfMe3 => XHSensorProfile.NrfMe3,
                ModelNumberNrfSgx => XHSensorProfile.NrfSgx,
                ModelNumberEfrSgx => XHSensorProfile.EfrSgx,
                _ => null,
            };
            return profile.HasValue
                ? new XHSensorProfileResolution(profile, "model_number", model, null)
                : new XHSensorProfileResolution(null, "unsupported", model, $"unrecognized model number \"{model}\"");
        }

        string? prefix = serialPrefix8?.ToUpperInvariant();
        if (prefix != null && KnownLegacySgxSerialPrefixes.Contains(prefix))
        {
            return new XHSensorProfileResolution(XHSensorProfile.NrfSgx, "known_sgx_serial", null, null);
        }
        return new XHSensorProfileResolution(XHSensorProfile.NrfMe3, "legacy_no_model_number", null, null);
    }
}
