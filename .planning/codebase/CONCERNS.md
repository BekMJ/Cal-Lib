# Codebase Concerns

## Scope
- Repository reviewed with focus on technical debt, correctness, security, and performance.
- Primary implementation files: `dotnet/XHale.Core/XHaleEngine.cs`, `dotnet/XHale.Core/IXHaleEngine.cs`, `dotnet/XHale.Core/XHale.Core.csproj`, `.github/workflows/publish.yml`.

## High-Priority Concerns

### 1) License key is effectively unchecked after initialization
- File: `dotnet/XHale.Core/XHaleEngine.cs`
- `Initialize(string licenseKey)` stores `_licenseKey`, but no later API enforces or validates it.
- Impact: callers can treat licensing as enforced when it is not; creates policy/compliance risk and weakens trust boundaries.
- Practical fix: either remove licensing surface until real enforcement exists, or gate operational methods (`FeedSample`, `AnalyzeBreath*`, baseline decode methods) behind explicit validation state.

### 2) Hard-coded medical/calibration coefficients with no provenance or versioning contract
- File: `dotnet/XHale.Core/XHaleEngine.cs`
- Large constant sets (including `DeviceGasCalibrations`) are embedded in code without metadata, source links, effective dates, or migration path.
- Impact: silent behavior drift risk, difficult audits, and potential regulatory/clinical traceability gaps.
- Practical fix: move coefficients to versioned configuration (or generated source from signed calibration assets), include schema/version fields and changelog references.

### 3) No automated tests for critical numerical behavior
- Files affected: entire `dotnet/XHale.Core/` surface
- No test project is present for thresholding, quantization boundaries (2.5/7.5/12.5), byte decode endianness, baseline lifecycle, or fit fallbacks.
- Impact: high regression risk for edge values and device-specific calibrations.
- Practical fix: add a `dotnet` test project with deterministic vectors for each path (`breath`, `gas-fit`, fallback, byte decode variants).

## Medium-Priority Concerns

### 4) Non-thread-safe mutable engine state
- File: `dotnet/XHale.Core/XHaleEngine.cs`
- `_samples`, `_baselineExplicit*`, and `_deviceSerialPrefix` mutate without synchronization.
- Impact: race conditions if SDK consumers call from multiple threads (common on mobile apps with BLE + UI pipelines).
- Practical fix: document single-thread requirement explicitly or make the type thread-safe (locking or immutable session snapshots).

### 5) Repeated allocations and sort in hot analysis path
- File: `dotnet/XHale.Core/XHaleEngine.cs`
- `AnalyzeBreath()` copies arrays, builds `List<double> deltas`, sorts for median, and uses additional arrays in `TrimmedMean` and fit logic.
- Impact: avoidable GC pressure for frequent real-time analysis.
- Practical fix: use span-based/pooled buffers, streaming quantile estimate for sample period, and avoid full sort when only median is needed.

### 6) O(n) front-removal in sample retention
- File: `dotnet/XHale.Core/XHaleEngine.cs`
- `FeedSample()` uses `_samples.RemoveRange(0, removeCount)` when exceeding `MaxSamples`.
- Impact: repeated shifting cost under sustained sampling.
- Practical fix: ring buffer/circular queue semantics for bounded sample windows.

### 7) API behavior mismatch risk around endianness defaults
- Files: `dotnet/XHale.Core/IXHaleEngine.cs`, `dotnet/XHale.Core/XHaleEngine.cs`
- Temperature decoding defaults differ by byte length (2-byte forced little-endian 0x2A6E assumption, 4-byte big-endian), while CO defaults are mostly big-endian.
- Impact: integration errors can silently produce wrong ppm without obvious failures.
- Practical fix: add explicit typed methods for protocol-specific formats and deprecate ambiguous overload defaults.

## Low-Priority Concerns

### 8) Production metadata placeholders in package manifest
- File: `dotnet/XHale.Core/XHale.Core.csproj`
- `Authors` is `Your Company` and `RepositoryUrl` is `https://example.invalid`.
- Impact: supply-chain trust, package discoverability, and supportability concerns.
- Practical fix: replace with real ownership/repository metadata before wider distribution.

### 9) Committed build artifacts increase repo noise and release ambiguity
- Files: `artifacts/XHale.Core.0.1.0-alpha.*.nupkg`
- Multiple packaged binaries are versioned in the repository.
- Impact: larger diffs/storage, unclear source-of-truth for releases, and potential accidental reuse of stale artifacts.
- Practical fix: publish artifacts via CI releases/package registry only; keep repo source-focused.

### 10) CI pipeline publishes on tag push without explicit test/verification stage
- File: `.github/workflows/publish.yml`
- Workflow performs restore/pack/publish but has no test gate.
- Impact: broken or regressed numerical logic can be shipped if tagging occurs prematurely.
- Practical fix: add mandatory `dotnet test` and optional static analysis/security scanning before publish step.

## Suggested Next Remediation Sequence
1. Add baseline automated tests for `XHaleEngine` numerical/byte-decoding behavior.
2. Decide and implement licensing contract (enforced or removed).
3. Replace sample storage with ring buffer and reduce allocation hotspots.
4. Externalize/version calibration tables with provenance metadata.
5. Harden CI release gate (`test` + checks) and clean committed artifacts/metadata.
