# Verification record

## 2026-08-30 — PNG parity stage

- Release solution build: passed with 0 warnings and 0 errors.
- Autonomous console suite: 25/25 groups passed.
- win-x64 framework-dependent published runner: 25/25 groups passed.
- linux-x64 self-contained published runner under WSL: 25/25 groups passed.
- PNG reference matrix: exact decoded pixels passed for color types 0/2/3/4/6, legal 1–16-bit depths, palette/tRNS, Adam7, filters 0–4, odd and one-pixel rows, and split IDAT chunks.
- Opaque RGB8 storage: exact RGB24 decode, RGB output color type 2, transparent output color type 6, and opacity classification passed.
- Forced scalar, SSSE3, AVX2, and automatic RGB Paeth paths: byte-identical output passed. Existing random-row and complete PNG/BMP/VP8L/VP8 SIMD tests also passed.
- Forced-serial and automatic rotation/resize/PNG-encode workloads: byte-identical output passed.
- 32 simultaneous transforms, caller stream ownership, malformed/resource-limit behavior, and native working-set stability: passed.
- Production project: no `PackageReference` and no ImageSharp reference.
- Production source: 36 C# files, 7,687 physical lines.
- Release assembly: 145,408 bytes, excluding PDB/XML/deps files.

The formal Windows x64 report used .NET 8.0.30, five warmups, and 15 measured iterations in isolated processes. It contains 35 scenarios: six JPEG transforms; three PNG component workloads; six PNG, six BMP, six VP8L, and six VP8 transforms; and two encoder-only workloads. Every row has finite metrics and matching dimensions. The HTML is self-contained, embeds the final and merged historical baseline JSON, escapes environment data, and has no external assets.

All nine PNG rows passed the blocking `png-parity` gate. Baseline-to-final speedups and final ratios against ImageSharp 3.1.12 were:

| Scenario | Baseline | Final | Baseline/final | Final/ImageSharp latency | Final/ImageSharp memory |
|---|---:|---:|---:|---:|---:|
| Decode 4000×3000 | 221.81 ms | 85.12 ms | 2.61× | 0.71× | 0.74× |
| Rotate 4000×3000 R90 | 85.39 ms | 13.27 ms | 6.44× | 0.46× | 0.75× |
| Encode 4000×3000 L6 | 709.04 ms | 125.84 ms | 5.63× | 0.38× | 0.77× |
| Thumbnail 200 R0 | 419.02 ms | 124.74 ms | 3.36× | 0.75× | 0.99× |
| Thumbnail 200 R90 | 484.32 ms | 139.63 ms | 3.47× | 0.73× | 0.59× |
| Thumbnail 800 R0 | 495.54 ms | 149.17 ms | 3.32× | 0.80× | 1.02× |
| Thumbnail 800 R90 | 578.52 ms | 177.29 ms | 3.26× | 0.81× | 0.65× |
| Thumbnail 1600 R0 | 710.59 ms | 239.35 ms | 2.97× | 0.83× | 1.07× |
| Thumbnail 1600 R90 | 838.41 ms | 288.94 ms | 2.90× | 0.86× | 0.55× |

Against the prior formal report, no existing JPEG/BMP/VP8L/VP8 or encoder-only row regressed by more than 5% in median latency or peak private bytes. The worst non-PNG latency ratio was 1.02×; the worst memory ratio was 1.02×. Shared adaptive resize and PNG encoding substantially improved many BMP, VP8L, VP8, and encoder-only rows.

The tracked [BenchmarkResults.html](BenchmarkResults.html) is the canonical benchmark record. JPEG rows intentionally use their final values as a 1.00× self-baseline; their primary comparison remains ImageSharp from the same isolated run.

## 2026-08-30 — JPEG parity stage

- Release solution build: passed with 0 warnings and 0 errors.
- Autonomous console suite: 25/25 groups passed.
- win-x64 framework-dependent published runner: 25/25 groups passed.
- linux-x64 self-contained published runner under WSL: 25/25 groups passed.
- Executable `--gate jpeg-parity` run with five warmups and 15 iterations: passed with exit code 0.
- Baseline/progressive gray, RGB/YCbCr sampling variants, CMYK/YCCK, restart markers, EXIF 1–8, truncated/corrupt inputs, alpha flattening, stream ownership, resource limits, 32 concurrent transforms, and native-memory stability: passed.
- Forced scalar, SSSE3, AVX2, automatic, and forced-serial/adaptive JPEG fixture pipelines: byte-identical results passed.
- The tracked 12 MP decoded-pixel hash remained unchanged through every retained decoder optimization. The quality-85 encoder output also remained byte-for-byte identical to the captured pre-stage encoder.
- Production project still has no `PackageReference` and no ImageSharp reference.
- Production source: 36 C# files, 7,698 physical lines.
- Release assembly: 160,256 bytes, excluding PDB/XML/deps files.

The definitive Windows x64 report used .NET 8.0.30, five warmups, and 15 measured iterations in isolated worker processes. It contains 39 scenarios: four JPEG components, six JPEG transforms, three PNG components, six PNG, six BMP, six VP8L, six VP8 transforms, and two encoder-only workloads. The HTML validator accepted all 39 rows, finite metrics, matching dimensions, embedded escaped JSON, and no external assets.

All blocking JPEG component and 1600 px rows passed the 1.05× latency and 1.10× peak-private limits against ImageSharp 3.1.12:

| Scenario | Tiesky | ImageSharp | Latency ratio | Memory ratio |
|---|---:|---:|---:|---:|
| Decode 4000×3000 | 119.42 ms | 134.90 ms | 0.89× | 0.69× |
| Rotate 4000×3000 R90 | 13.34 ms | 29.19 ms | 0.46× | 0.60× |
| Resize predecoded 12 MP to 1600 px | 53.98 ms | 60.59 ms | 0.89× | 0.96× |
| Encode 4000×3000 Q85 | 114.38 ms | 141.56 ms | 0.81× | 0.64× |
| JPEG→JPEG 1600 R0 | 153.12 ms | 209.03 ms | 0.73× | 1.06× |
| JPEG→JPEG 1600 R90 | 163.57 ms | 239.40 ms | 0.68× | 0.77× |

The existing 1600×1200 encoder-only row improved from 76.25 ms to 21.27 ms and now runs at 0.81× ImageSharp latency with 1.08× memory. The two formal 1600 transform rows improved from 301.90/308.34 ms to 153.12/163.57 ms (1.97×/1.89×). Captured pre-stage probes also moved full decode from 328.23 ms to 119.42 ms and full encode from 421.97 ms to 114.38 ms. Encoded sizes and quality did not regress.

The combined formal run had one VP8L latency sample and several VP8 peak-private samples outside the 5% historical-noise band even though no relevant decoder path changed. Isolated 5×15 repeats resolved the variance: VP8 1600 R0/R90 measured 0.94×/0.95× baseline latency and 1.03×/1.03× memory; VP8L 1600 R0/R90 measured 1.01×/0.99× latency and 1.00×/0.99× memory. The retained four-worker JPEG encoder cap is what keeps these shared JPEG-output scenarios bounded.

The regenerated [BenchmarkResults.html](BenchmarkResults.html) is the canonical 39-row result. JPEG transform rows still display their documented final self-baseline as 1.00× in the main table; the separate JPEG pre-optimization table contains the actual captured transform improvements, and the component table provides the direct ImageSharp comparison.

## 2026-08-30 — VP8L and VP8 parity stage

- Release solution build: passed with 0 warnings and 0 errors.
- Autonomous console suite: 26/26 groups passed.
- win-x64 framework-dependent published runner: 26/26 groups passed.
- linux-x64 self-contained published runner under WSL: 26/26 groups passed.
- Executable `--gate webp-parity` run with five warmups and 15 iterations: all 20 WebP component/transform rows passed.
- The tracked oracle contains 43 spec-valid fixtures: 31 VP8L and 12 VP8, including 17 opaque and 26 alpha-bearing images. All VP8L RGBA SHA-256 values exactly match ImageSharp; VP8 retains exact Tiesky golden hashes and passes the existing per-pixel perceptual thresholds.
- Three deliberately truncated RIFF vectors remain corruption tests and are intentionally excluded from the valid manifest.
- Forced scalar, SSE2/SSSE3, AVX2, automatic, and forced-serial/adaptive complete WebP pipelines: byte-identical results passed, including both tracked 12 MP fixtures.
- Thirty-two simultaneous mixed PNG/VP8L/VP8 transforms, caller stream ownership, malformed/resource-limit behavior, alpha classification, and native-memory stability: passed.
- Production project still has no `PackageReference` and no ImageSharp reference.
- Production source: 33 C# files, 8,138 physical lines.
- Release assembly: 167,424 bytes, excluding PDB/XML/deps files.

The definitive Windows x64 HTML uses .NET 8.0.30, five warmups, and 15 measured iterations in isolated worker processes. It contains 47 scenarios: four components plus six transforms for JPEG, VP8L, and VP8; three PNG components and six PNG transforms; six BMP transforms; and two encoder-only rows. Validation accepted all 47 rows, finite metrics, matching dimensions, embedded escaped JSON, and no external assets. WebP output-component labels explicitly describe VP8L→PNG level 6 and VP8→JPEG quality 85; no WebP encoder is implied.

All eight WebP components passed the 1.05× latency and 1.10× peak-private limits against ImageSharp 3.1.12 in the formal gated run:

| Scenario | Pre-WebP baseline | Final | ImageSharp | Baseline/final | Final/ImageSharp latency | Final/ImageSharp memory |
|---|---:|---:|---:|---:|---:|---:|
| VP8L decode 4000×3000 | 250.87 ms | 109.75 ms | 145.38 ms | 2.29× | 0.75× | 0.60× |
| VP8L rotate 4000×3000 R90 | 14.80 ms | 13.74 ms | 32.30 ms | 1.08× | 0.43× | 0.74× |
| VP8L resize predecoded 12 MP to 1600 px | 61.80 ms | 59.50 ms | 61.16 ms | 1.04× | 0.97× | 0.99× |
| VP8L output to PNG 4000×3000 L6 | 231.57 ms | 134.86 ms | 531.34 ms | 1.72× | 0.25× | 0.65× |
| VP8 decode 4000×3000 | 697.85 ms | 392.56 ms | 476.09 ms | 1.78× | 0.82× | 0.72× |
| VP8 rotate 4000×3000 R90 | 14.48 ms | 14.49 ms | 32.83 ms | 1.00× | 0.44× | 0.64× |
| VP8 resize predecoded 12 MP to 1600 px | 58.28 ms | 57.85 ms | 61.00 ms | 1.01× | 0.95× | 0.71× |
| VP8 output to JPEG 4000×3000 Q85 | 121.37 ms | 122.10 ms | 135.54 ms | 0.99× | 0.90× | 1.05× |

Across the twelve final WebP transformations, latency ratios ranged from 0.64× to 0.88× ImageSharp and memory ratios from 0.46× to 0.74×. Output sizes are unchanged from the captured pre-WebP baseline; VP8 PSNR/golden hashes and VP8L exact pixels are unchanged.

The first packed-RGB24 intermediate experiment regressed PNG 200 R0 and was rejected. The retained design uses aligned RGBA32 separable intermediates with compact RGB24 final output. Isolated repeats against the actual pre-WebP `HEAD` report place paired PNG resize medians at 0.88×–1.04× baseline, BMP rows at 0.96×–1.05×, and JPEG transform rows at 0.93×–1.01×; peak-private ratios remain within the same 5% band after repeat aggregation. Combined-suite outliers were confined to stage-wide runtime/private-byte sampling variance and are published unchanged in the HTML.

The regenerated [BenchmarkResults.html](BenchmarkResults.html) is the canonical 47-row result. It embeds the merged historical baseline, includes WebP baseline-to-final tables, and keeps JPEG transform self-baselines at the documented 1.00×.

## 2026-08-30 — ordered pipeline and image information API

- Release solution build: passed with 0 warnings and 0 errors.
- Autonomous console suite: 29/29 groups passed.
- win-x64 framework-dependent published runner: 29/29 groups passed.
- linux-x64 self-contained published runner under WSL: 29/29 groups passed.
- Example console smoke run: passed and produced all six expected PNG/JPEG outputs.
- All 12 input/output combinations (byte array, span, or stream to byte array, returned `MemoryStream`, caller stream, or `IBufferWriter<byte>`) produced byte-identical results for equivalent calls.
- Empty, repeated, and differently ordered transformations; EXIF-before-user-transform behavior; list/option snapshots; mandatory encoder validation; unsupported implementations; nulls; dimensions; and read limits: passed.
- JPEG, PNG, BMP, VP8, VP8L, EXIF orientations 1–8, animated WebP/APNG, malformed/truncated inputs, seekable position restoration, and non-seekable consumption behavior: passed through header-only identification tests.
- Returned `MemoryStream` ownership and position, caller stream lifetime/current-position output, 32 concurrent calls, scalar/SIMD equivalence, serial/adaptive equivalence, and native working-set stability: passed.
- Existing `TransformOptions` output is byte-identical to the corresponding ordered pipeline call.
- Production project still has no `PackageReference` and no ImageSharp reference.
- Production source: 44 C# files, 9,204 physical lines.
- Release assembly: 183,808 bytes, excluding PDB/XML/deps files.

The formal Windows x64 benchmark was rerun with five warmups and 15 measured iterations. The tracked HTML contains 47 final scenarios, six JPEG transformation rows, finite median values, matching dimensions, embedded escaped JSON, and no external assets. It retains 43 historical baseline scenarios and the documented JPEG self-baseline policy. Back-to-back component samples showed machine-temperature/runtime variability in unchanged direct rotation paths; the raw measurements are published without replacing them with favorable repeats.

The regenerated [BenchmarkResults.html](BenchmarkResults.html) remains the canonical performance record. Its reproduction command explicitly reuses the tracked report as the historical baseline so future runs do not discard those values.
