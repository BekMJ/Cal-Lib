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

// 1. nRF-SGX by Model Number, bundled gain, warm breath: gas path, not human formula
var e1 = New("A793BAFC11112222", "XHale-NRF-SGX");
var rWarm = e1.AnalyzeBreath(co, tWarm, 1.0);
var rFlat = e1.AnalyzeBreath(co, tFlat, 1.0);
Check(!rWarm.PredictionBlocked && rWarm.SensorProfile == "nrf_sgx" && rWarm.SensorProfileSource == "model_number", "sgx model profile");
Check(rWarm.EstimatedPPM == 10.0, $"sgx warm reads 10 (got {rWarm.EstimatedPPM})");
Check(rWarm.EstimatedPPM == rFlat.EstimatedPPM, "sgx ppm independent of temperature rise");
// Same data on a Winsen device with the same constants injected takes the human branch when warm
var w = New("12345678", null); w.SetPerDeviceGasCalibration("12345678", 0, 327.436077, 1, 0);
var rw = w.AnalyzeBreath(co, tWarm, 1.0);
Check(rw.SensorProfile == "nrf_me3" && rw.EstimatedPPM != 10.0, $"winsen warm uses human formula (got {rw.EstimatedPPM})");

// 2. Known SGX prototype without Model Number
var e2 = New("D0F18121", null);
var r2 = e2.AnalyzeBreath(Make(307.666794, 10).co, tWarm, 1.0);
Check(r2.SensorProfile == "nrf_sgx" && r2.SensorProfileSource == "known_sgx_serial" && r2.EstimatedPPM == 10.0, $"known serial sgx (got {r2.EstimatedPPM}, {r2.SensorProfileSource})");

// 3. 0DC44112: SGX without bundled gain -> blocked; injected -> works
var e3 = New("0DC44112", null);
var r3 = e3.AnalyzeBreath(co, tFlat, 1.0);
Check(r3.PredictionBlocked && r3.MissingGasCalibration && r3.EstimatedPPM == 0, "uncalibrated sgx blocked");
e3.SetPerDeviceGasCalibration("0DC44112", 0, 327.436077, 1, 0);
Check(!e3.AnalyzeBreath(co, tFlat, 1.0).PredictionBlocked, "injected sgx calibration unblocks");
// firmware calibration also unblocks
var e3b = New("0DC44112", null); e3b.SetFirmwareGasCalibration(new XHDeviceGasCalibration(0, 327.436077, 1, 0));
Check(e3b.AnalyzeBreath(co, tFlat, 1.0).EstimatedPPM == 10.0, "firmware sgx calibration unblocks");

// 4. Unknown serial reporting nRF-SGX: never Winsen generic fallback
var r4 = New("ABCDEF01", "XHale-NRF-SGX").AnalyzeBreath(co, tFlat, 1.0);
Check(r4.PredictionBlocked && !r4.UsedGenericGasCalibration, "sgx never uses generic winsen fallback");

// 5. EFR + SGX recognized but blocked
var r5 = New("A793BAFC", "XHale-EFR-SGX").AnalyzeBreath(co, tFlat, 1.0);
Check(r5.PredictionBlocked && r5.SensorProfile == "efr_sgx", "efr blocked");
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
bool threw = false; try { e9b.DecodeCoPpmFromBytes(Be(31000), Temp(22.0)); } catch (InvalidOperationException) { threw = true; }
Check(threw, "uncalibrated sgx single-sample throws");
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
Check(e13.AnalyzeBreath(co, tFlat, 1.0).EstimatedPPM == 10.0, "device A uses its firmware calibration");
e13.ClearDeviceIdentity(); e13.SetDeviceIdentity("BBBBBBBB", "XHale-NRF-SGX");
var r13 = e13.AnalyzeBreath(co, tFlat, 1.0);
Check(r13.PredictionBlocked && r13.MissingGasCalibration && r13.EstimatedPPM == 0, "clear + new identity drops A's firmware calibration");
// Switching identity directly (no Clear) also drops it
var e13b = new XHaleEngine(); e13b.Initialize("k");
e13b.SetDeviceIdentity("AAAAAAAA", "XHale-NRF-SGX"); e13b.SetFirmwareGasCalibration(fwCal);
e13b.SetDeviceIdentity("BBBBBBBB", "XHale-NRF-SGX");
Check(e13b.AnalyzeBreath(co, tFlat, 1.0).PredictionBlocked, "direct identity switch drops firmware calibration");
// Serial-only API path
var e13c = New("AAAAAAAA", "XHale-NRF-SGX"); e13c.SetFirmwareGasCalibration(fwCal);
e13c.SetDeviceSerial("BBBBBBBB"); e13c.SetModelNumber("XHale-NRF-SGX");
Check(e13c.AnalyzeBreath(co, tFlat, 1.0).PredictionBlocked, "serial switch drops firmware calibration");
// Same device re-identified keeps it; calibration read before identity belongs to the incoming device
var e13d = new XHaleEngine(); e13d.Initialize("k");
e13d.SetFirmwareGasCalibration(fwCal); e13d.SetDeviceIdentity("CCCCCCCC", "XHale-NRF-SGX");
Check(e13d.AnalyzeBreath(co, tFlat, 1.0).EstimatedPPM == 10.0, "calibration before identity is kept");
e13d.SetDeviceIdentity("CCCCCCCC", "XHale-NRF-SGX");
Check(e13d.AnalyzeBreath(co, tFlat, 1.0).EstimatedPPM == 10.0, "same identity keeps calibration");

Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
