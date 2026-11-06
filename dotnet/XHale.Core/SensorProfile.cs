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
    /// <summary>EFR + SGX. Same raw CO format and gas fit as nRF + SGX. Uses its own calibration (firmware CAL_READ
    /// or injected), else the EFR-SGX fallback gain; never the nRF-SGX gain or the bundled nRF table.</summary>
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
    /// EFR + SGX produces ppm. Its firmware streams CO in the same format as nRF + SGX and stores its own
    /// calibration in the same CAL_READ record. Without firmware or injected constants
    /// the EFR-SGX fallback gain (<see cref="EfrSgxFallbackGainRawPerPpm"/>) is used.
    /// </summary>
    public const bool EfrSgxPredictionEnabled = true;

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

    /// <summary>The bundled per-serial table holds nRF-board fits; EFR + SGX uses only firmware or injected constants.</summary>
    public static bool AllowsBundledNrfCalibrationTable(XHSensorProfile profile) => profile != XHSensorProfile.EfrSgx;

    /// <summary>SGX boards may use their board-specific SGX fallback gain when no device calibration exists.</summary>
    public static bool AllowsSgxFallbackCalibration(XHSensorProfile profile) => SgxFallbackGainRawPerPpm(profile).HasValue;

    /// <summary>Board-specific SGX fallback gain (raw counts per ppm), or null for Winsen. nRF and EFR values are never swapped.</summary>
    public static double? SgxFallbackGainRawPerPpm(XHSensorProfile profile) => profile switch
    {
        XHSensorProfile.NrfSgx => NrfSgxFallbackGainRawPerPpm,
        XHSensorProfile.EfrSgx => EfrSgxFallbackGainRawPerPpm,
        _ => null,
    };

    /// <summary>
    /// EFR + SGX fallback gain (raw counts per ppm): app-matched fit of EFR-SGX 60A423FF (0 and 10 ppm, 2026-11-06).
    /// Used with drift 0, tau 1 s, dead time 0.
    /// </summary>
    public const double EfrSgxFallbackGainRawPerPpm = 190.6177199102797;

    /// <summary>
    /// nRF + SGX fallback gain (raw counts per ppm): mean of the two app-matched nRF-SGX fits provisioned 2026-10-09
    /// (A793BAFC 327.436077, D0F18121 307.666794). Used with drift 0, tau 1 s, dead time 0.
    /// </summary>
    public const double NrfSgxFallbackGainRawPerPpm = (327.436077 + 307.666794) / 2.0;

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
        => Resolve(modelNumber, serialPrefix8, modelNumberLookupFailed: false);

    /// <summary>
    /// Resolves the processing profile, distinguishing a Model Number that is confirmed absent from one
    /// whose BLE read failed. A failed lookup is NOT treated as "absent" (that would give a new SGX device
    /// Winsen processing): it is unsupported, except for the known SGX prototype serials.
    /// </summary>
    /// <param name="modelNumber">Value read from 0x2A24, or null if absent or not read.</param>
    /// <param name="serialPrefix8">Normalized 8-character serial prefix, if known.</param>
    /// <param name="modelNumberLookupFailed">True when 0x2A24 exists (or may exist) but discovery/read failed.</param>
    public static XHSensorProfileResolution Resolve(string? modelNumber, string? serialPrefix8, bool modelNumberLookupFailed)
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
        if (modelNumberLookupFailed)
        {
            return new XHSensorProfileResolution(null, "unsupported", null,
                "device model could not be read; reconnect and try again");
        }
        return new XHSensorProfileResolution(XHSensorProfile.NrfMe3, "legacy_no_model_number", null, null);
    }
}
