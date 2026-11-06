using XHale.Core;
int fail = 0, pass = 0;
void Check(bool ok, string name) { if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + name); } }

// Synthetic SGX gas run: 20 s at 1 Hz, flat baseline then 10 ppm step response (tau 1 s), gain 327.436077
(double[] co, double[] tFlat, double[] tWarm) Make(double gain, double ppm)
{
    int n = 21; var co = new double[n]; var tf = new double[n]; var tw = new double[n];
    for (int i = 0; i < n; i++)
    {
        double u = Math.Max(0, i - 2);
        co[i] = 30000 + (i >= 2 ? gain * ppm * (1 - Math.Exp(-u / 1.0)) : 0);
        tf[i] = 22.0;
        tw[i] = 22.0 + (i >= 3 ? Math.Min(8.0, (i - 3) * 1.0) : 0);   // breath-like +8 C rise
    }
    return (co, tf, tw);
}
XHaleEngine New(string? serial, string? model, bool setModel = true)
{
    var e = new XHaleEngine(); e.Initialize("k"); e.SetDeviceSerial(serial); if (setModel) e.SetModelNumber(model); return e;
}
var (co, tFlat, tWarm) = Make(327.436077, 10);
// SGX readings are continuous (no 0/5/10/15 bucketing); synthetic 10 ppm reads close to 10.
static bool Near10(double ppm) => Math.Abs(ppm - 10.0) < 0.6;

// 1. nRF-SGX by Model Number, bundled gain, warm breath: gas path, not human formula
var e1 = New("A793BAFC11112222", "XHale-NRF-SGX");
var rWarm = e1.AnalyzeBreath(co, tWarm, 1.0);
var rFlat = e1.AnalyzeBreath(co, tFlat, 1.0);
Check(!rWarm.PredictionBlocked && rWarm.SensorProfile == "nrf_sgx" && rWarm.SensorProfileSource == "model_number", "sgx model profile");
Check(Near10(rWarm.EstimatedPPM) && rWarm.EstimatedPPM != 10.0, $"sgx warm reads ~10, not bucketed (got {rWarm.EstimatedPPM})");
Check(rWarm.EstimatedPPM == rFlat.EstimatedPPM, "sgx ppm independent of temperature rise");
// Same data on a Winsen device with the same constants injected takes the human branch when warm
var w = New("12345678", null); w.SetPerDeviceGasCalibration("12345678", 0, 327.436077, 1, 0);
var rw = w.AnalyzeBreath(co, tWarm, 1.0);
Check(rw.SensorProfile == "nrf_me3" && rw.EstimatedPPM != 10.0, $"winsen warm uses human formula (got {rw.EstimatedPPM})");
// Winsen gas path still rounds to the 0/5/10/15 buckets; SGX with the same constants does not
var rwFlat = w.AnalyzeBreath(co, tFlat, 1.0);
Check(rwFlat.EstimatedPPM == 10.0 && rFlat.EstimatedPPM != 10.0, $"winsen gas bucketed, sgx continuous (winsen {rwFlat.EstimatedPPM}, sgx {rFlat.EstimatedPPM})");

// 2. Known SGX prototype without Model Number
var e2 = New("D0F18121", null);
var r2 = e2.AnalyzeBreath(Make(307.666794, 10).co, tWarm, 1.0);
Check(r2.SensorProfile == "nrf_sgx" && r2.SensorProfileSource == "known_sgx_serial" && Near10(r2.EstimatedPPM), $"known serial sgx (got {r2.EstimatedPPM}, {r2.SensorProfileSource})");

// 3. 0DC44112: SGX without bundled gain -> nRF-SGX fallback gain; injected -> its own gain
var e3 = New("0DC44112", null);
var r3 = e3.AnalyzeBreath(co, tFlat, 1.0);
Check(!r3.PredictionBlocked && r3.UsedSgxFallbackCalibration && !r3.UsedGenericGasCalibration && Near10(r3.EstimatedPPM), $"uncalibrated nrf-sgx uses sgx fallback (got {r3.EstimatedPPM})");
e3.SetPerDeviceGasCalibration("0DC44112", 0, 327.436077, 1, 0);
var r3i = e3.AnalyzeBreath(co, tFlat, 1.0);
Check(!r3i.PredictionBlocked && !r3i.UsedSgxFallbackCalibration, "injected sgx calibration replaces fallback");
// firmware calibration also unblocks
var e3b = New("0DC44112", null); e3b.SetFirmwareGasCalibration(new XHDeviceGasCalibration(0, 327.436077, 1, 0));
Check(Near10(e3b.AnalyzeBreath(co, tFlat, 1.0).EstimatedPPM), "firmware sgx calibration unblocks");

// 4. Unknown serial reporting nRF-SGX: SGX fallback, never Winsen generic fallback
var r4 = New("ABCDEF01", "XHale-NRF-SGX").AnalyzeBreath(co, tFlat, 1.0);
Check(!r4.PredictionBlocked && r4.UsedSgxFallbackCalibration && !r4.UsedGenericGasCalibration, "sgx never uses generic winsen fallback");
// Fallback = gain 317.55, drift 0, tau 1, dead 0: identical to injecting those constants by hand
Check(Math.Abs(XHSensorProfiles.NrfSgxFallbackGainRawPerPpm - 317.5514355) < 1e-6, "sgx fallback gain is mean of app-matched gains");
var co20 = Make(300.0, 20).co;
var r4b = New("ABCDEF01", "XHale-NRF-SGX").AnalyzeBreath(co20, tFlat, 1.0);
var e4c = New("ABCDEF01", "XHale-NRF-SGX"); e4c.SetPerDeviceGasCalibration("ABCDEF01", 0, 317.5514355, 1, 0);
var r4c = e4c.AnalyzeBreath(co20, tFlat, 1.0);
Check(r4b.UsedSgxFallbackCalibration && r4b.EstimatedPPM == r4c.EstimatedPPM && r4b.EstimatedPPM > 17, $"sgx fallback gain applied (got {r4b.EstimatedPPM} vs {r4c.EstimatedPPM})");

// 5. EFR + SGX: firmware/injected calibration, else the EFR fallback gain; never the bundled nRF table or nRF gain
Check(Math.Abs(XHSensorProfiles.EfrSgxFallbackGainRawPerPpm - 190.6177199) < 1e-6, "efr fallback gain value");
var r5 = New("12345678", "XHale-EFR-SGX").AnalyzeBreath(co20, tFlat, 1.0);
var e5x = New("12345678", "XHale-EFR-SGX"); e5x.SetPerDeviceGasCalibration("12345678", 0, 190.6177199102797, 1, 0);
var r5x = e5x.AnalyzeBreath(co20, tFlat, 1.0);
Check(!r5.PredictionBlocked && r5.UsedSgxFallbackCalibration && r5.SensorProfile == "efr_sgx" && r5.EstimatedPPM == r5x.EstimatedPPM && r5.EstimatedPPM != r4b.EstimatedPPM,
    $"uncalibrated efr uses efr fallback gain, not nrf (got {r5.EstimatedPPM} vs {r5x.EstimatedPPM}, nrf {r4b.EstimatedPPM})");
var r5t = New("A793BAFC", "XHale-EFR-SGX").AnalyzeBreath(co20, tFlat, 1.0);
Check(r5t.UsedSgxFallbackCalibration && r5t.EstimatedPPM == r5.EstimatedPPM, "efr does not use bundled nRF table");
var e5f = New("12345678", "XHale-EFR-SGX"); e5f.SetFirmwareGasCalibration(new XHDeviceGasCalibration(0, 317.5514355, 1, 0));
var r5f = e5f.AnalyzeBreath(co20, tFlat, 1.0);
Check(!r5f.PredictionBlocked && !r5f.UsedSgxFallbackCalibration && r5f.EstimatedPPM == r4c.EstimatedPPM, $"efr uses firmware calibration (got {r5f.EstimatedPPM} vs {r4c.EstimatedPPM})");
var e5i = New("12345678", "XHale-EFR-SGX"); e5i.SetPerDeviceGasCalibration("12345678", 0, 317.5514355, 1, 0);
Check(!e5i.AnalyzeBreath(co20, tFlat, 1.0).PredictionBlocked, "efr uses injected calibration");
Check(New("A793BAFC", "XHale-ESP-SGX").GetSensorProfile().Profile == null, "old ESP name is unsupported");

// 6. Unknown model -> unsupported
var r6 = New("12345678", "XHale-FOO").AnalyzeBreath(co, tFlat, 1.0);
Check(r6.PredictionBlocked && r6.SensorProfile == "unsupported", "unknown model unsupported");

// 7. Model Number normalization and legacy default
Check(New("12345678", "XHale-NRF-SGX\0 ").GetSensorProfile().Profile == XHSensorProfile.NrfSgx, "normalize NUL");
Check(New("12345678", null, setModel: false).GetSensorProfile().Profile == XHSensorProfile.NrfMe3, "no model -> winsen");
Check(New("a793bafc", null).GetSensorProfile().Source == "known_sgx_serial", "known serial case-insensitive");

// 8. Switching device clears model; re-setting same serial keeps it
var e8 = New("11111111", "XHale-NRF-SGX");
e8.SetDeviceSerial("11111111");
Check(e8.GetSensorProfile().Profile == XHSensorProfile.NrfSgx, "same serial keeps model");
e8.SetDeviceSerial("22222222");
Check(e8.GetSensorProfile().Profile == XHSensorProfile.NrfMe3, "new serial clears model");

// 9. Single-sample decode: SGX ignores warm-breath branch, uses gain
byte[] Be(int v) => new[] { (byte)(v >> 8), (byte)(v & 0xFF) };
byte[] Temp(double c) { short s = (short)Math.Round(c * 100); return new[] { (byte)(s & 0xFF), (byte)((s >> 8) & 0xFF) }; }
var e9 = New("A793BAFC", "XHale-NRF-SGX"); e9.SetBaseline(30000, 22.0);
double p9 = e9.DecodeCoPpmFromBytes(Be(30000 + 3274), Temp(30.0));
Check(Math.Abs(p9 - 3274 / 327.436077) < 1e-9, $"sgx single-sample uses gain (got {p9})");
var e9b = New("12345678", "XHale-NRF-SGX"); e9b.SetBaseline(30000, 22.0);
double p9b = e9b.DecodeCoPpmFromBytes(Be(31000), Temp(22.0));
Check(Math.Abs(p9b - 1000 / 317.5514355) < 1e-6, $"uncalibrated nrf-sgx single-sample uses sgx fallback (got {p9b})");
var e9d = New("12345678", "XHale-EFR-SGX"); e9d.SetBaseline(30000, 22.0);
double p9d = e9d.DecodeCoPpmFromBytes(Be(31000), Temp(22.0));
Check(Math.Abs(p9d - 1000 / 190.6177199102797) < 1e-9, $"uncalibrated efr-sgx single-sample uses efr fallback (got {p9d})");
var e9e = New("12345678", "XHale-EFR-SGX"); e9e.SetFirmwareGasCalibration(new XHDeviceGasCalibration(0, 300.0, 1, 0)); e9e.SetBaseline(30000, 22.0);
Check(Math.Abs(e9e.DecodeCoPpmFromBytes(Be(30600), Temp(22.0)) - 2.0) < 1e-9, "efr-sgx single-sample uses firmware gain");
var e9c = New("12345678", null); e9c.SetBaseline(30000, 22.0);
Check(e9c.DecodeCoPpmFromBytes(Be(31000), Temp(30.0)) == Math.Max(0, ((1000 - 17.30 * 8.0) - 63.20735653235655) / 11.558260683760434), "winsen single-sample unchanged");

// 10. Model Number before serial (BLE read order) must not be lost
var e10 = new XHaleEngine(); e10.Initialize("k");
e10.SetModelNumber("XHale-NRF-SGX"); e10.SetDeviceSerial("ABCDEF01");
Check(e10.GetSensorProfile().Profile == XHSensorProfile.NrfSgx, "model before serial kept");
e10.ClearDeviceIdentity(); e10.SetModelNumber("XHale-EFR-SGX"); e10.SetDeviceSerial("12345678");
Check(e10.GetSensorProfile().Profile == XHSensorProfile.EfrSgx, "model before serial after clear");
e10.SetDeviceSerial(null); e10.SetDeviceSerial("12345678");
Check(e10.GetSensorProfile().Profile == XHSensorProfile.NrfMe3, "null serial clears model");
// 11. Atomic identity
var e11 = new XHaleEngine(); e11.Initialize("k");
e11.SetDeviceIdentity("11111111", "XHale-NRF-SGX");
Check(e11.GetSensorProfile().Profile == XHSensorProfile.NrfSgx, "identity sets sgx");
e11.SetDeviceIdentity("22222222", null);
Check(e11.GetSensorProfile().Profile == XHSensorProfile.NrfMe3, "identity replaces model");
// 12. Blocked results do not raise breath-quality flags
Check(!r6.ShortDuration && !r6.SmallTemperatureRise, "blocked result quality flags false");

// 13. Firmware calibration must not leak to the next device (B's CAL_READ fails, so none is set)
var fwCal = new XHDeviceGasCalibration(0, 327.436077, 1, 0);
var e13 = new XHaleEngine(); e13.Initialize("k");
e13.SetDeviceIdentity("AAAAAAAA", "XHale-NRF-SGX"); e13.SetFirmwareGasCalibration(fwCal);
Check(Near10(e13.AnalyzeBreath(co, tFlat, 1.0).EstimatedPPM), "device A uses its firmware calibration");
e13.ClearDeviceIdentity(); e13.SetDeviceIdentity("BBBBBBBB", "XHale-NRF-SGX");
var r13 = e13.AnalyzeBreath(co, tFlat, 1.0);
Check(r13.UsedSgxFallbackCalibration, "clear + new identity drops A's firmware calibration (B uses sgx fallback)");
// Switching identity directly (no Clear) also drops it
var e13b = new XHaleEngine(); e13b.Initialize("k");
e13b.SetDeviceIdentity("AAAAAAAA", "XHale-NRF-SGX"); e13b.SetFirmwareGasCalibration(fwCal);
e13b.SetDeviceIdentity("BBBBBBBB", "XHale-NRF-SGX");
Check(e13b.AnalyzeBreath(co, tFlat, 1.0).UsedSgxFallbackCalibration, "direct identity switch drops firmware calibration");
// Serial-only API path
var e13c = New("AAAAAAAA", "XHale-NRF-SGX"); e13c.SetFirmwareGasCalibration(fwCal);
e13c.SetDeviceSerial("BBBBBBBB"); e13c.SetModelNumber("XHale-NRF-SGX");
Check(e13c.AnalyzeBreath(co, tFlat, 1.0).UsedSgxFallbackCalibration, "serial switch drops firmware calibration");
// Same device re-identified keeps it; calibration read before identity belongs to the incoming device
var e13d = new XHaleEngine(); e13d.Initialize("k");
e13d.SetFirmwareGasCalibration(fwCal); e13d.SetDeviceIdentity("CCCCCCCC", "XHale-NRF-SGX");
Check(Near10(e13d.AnalyzeBreath(co, tFlat, 1.0).EstimatedPPM), "calibration before identity is kept");
e13d.SetDeviceIdentity("CCCCCCCC", "XHale-NRF-SGX");
Check(Near10(e13d.AnalyzeBreath(co, tFlat, 1.0).EstimatedPPM), "same identity keeps calibration");

// 14. Model Number lookup failed is NOT "absent"
var e14 = new XHaleEngine(); e14.Initialize("k");
e14.SetDeviceIdentity("99999999", null, modelNumberLookupFailed: true);
var r14 = e14.AnalyzeBreath(co, tWarm, 1.0);
Check(r14.PredictionBlocked && r14.SensorProfile == "unsupported" && r14.EstimatedPPM == 0, "failed lookup on unknown serial is blocked, not winsen");
Check(r14.PredictionBlockedReason!.Contains("Reconnect"), "failed lookup reason asks to reconnect");
e14.SetDeviceIdentity("D0F18121", null, modelNumberLookupFailed: true);
Check(e14.GetSensorProfile().Profile == XHSensorProfile.NrfSgx, "failed lookup keeps known sgx serial");
e14.SetDeviceIdentity("99999999", "XHale-NRF-SGX", modelNumberLookupFailed: true);
Check(e14.GetSensorProfile().Profile == XHSensorProfile.NrfSgx, "a read model number overrides the failed flag");
e14.SetDeviceIdentity("99999999", null);
Check(e14.GetSensorProfile().Profile == XHSensorProfile.NrfMe3, "2-arg identity with null model is confirmed absent (winsen)");
var e14b = New("99999999", null); e14b.SetModelNumberLookupFailed();
Check(e14b.GetSensorProfile().Profile == null, "SetModelNumberLookupFailed blocks unknown serial");
e14b.ClearDeviceIdentity(); e14b.SetDeviceSerial("99999999");
Check(e14b.GetSensorProfile().Profile == XHSensorProfile.NrfMe3, "ClearDeviceIdentity clears failed flag");

// 15. Sample-only input (baseline set after warmup, arrays hold just the breath sample) matches the iOS app
//     20 samples at 1 Hz: 7 flat samples (22 C, 30000 raw), then temperature +1 C/s up to 30 C and CO rising to +2000.
int ns = 20; var coS = new double[ns]; var tS = new double[ns];
for (int i = 0; i < ns; i++)
{
    tS[i] = i < 7 ? 22.0 : Math.Min(30.0, 22.0 + (i - 6));
    coS[i] = i < 7 ? 30000 : 30000 + 2000.0 * (i - 6) / (ns - 7);
}
// App: T0 = mean(first 10) = 22.6 -> breath start at first temp >= 23.4 (index 8, 24 C); 8 pre-breath samples.
double tBaseApp = (7 * 22.0 + 23.0) / 8.0;
double rBaseApp = (7 * 30000 + coS[7]) / 8.0;   // 8 < 10 samples: app trims none
double expectedApp = Math.Max(0, ((coS[ns - 1] - rBaseApp) - 17.30 * (30.0 - tBaseApp) - 63.20735653235655) / 11.558260683760434);
var e15 = New("12345678", null); e15.SetBaseline(30000, 22.0);
var r15 = e15.AnalyzeBreath(coS, tS, 1.0);
Check(Math.Abs(r15.EstimatedPPM - expectedApp) < 1e-9, $"sample-only winsen breath matches app (got {r15.EstimatedPPM}, want {expectedApp})");
Check(Math.Abs(r15.DurationSec - (ns - 1 - 8)) < 1e-9, $"sample-only duration = breath start to last sample (got {r15.DurationSec})");
var r15legacy = New("12345678", null).AnalyzeBreath(coS, tS, 1.0);
Check(r15legacy.EstimatedPPM != r15.EstimatedPPM, $"legacy warmup-window mode differs on a sample-only array (legacy {r15legacy.EstimatedPPM})");
// SGX gas path is unaffected by the baseline mode (the fit anchors on the first two samples)
var e15s = New("A793BAFC11112222", "XHale-NRF-SGX"); e15s.SetBaseline(30000, 22.0);
Check(e15s.AnalyzeBreath(co, tWarm, 1.0).EstimatedPPM == rWarm.EstimatedPPM, "sample-only sgx gas fit unchanged");

// 16. FeedSample keeps sub-second timestamps (100 ms spacing -> 0.1 s period, same as the array API)
var e16 = New("12345678", null); var e16a = New("12345678", null);
var t0f = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
for (int i = 0; i < ns; i++) e16.FeedSample(coS[i], tS[i], 50, t0f.AddMilliseconds(100 * i));
var r16 = e16.AnalyzeBreath(); var r16a = e16a.AnalyzeBreath(coS, tS, 0.1);
Check(Math.Abs(r16.EstimatedPPM - r16a.EstimatedPPM) < 1e-6 && Math.Abs(r16.DurationSec - r16a.DurationSec) < 1e-4, $"FeedSample uses 0.1 s period (got {r16.DurationSec} s vs {r16a.DurationSec} s)");

Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
