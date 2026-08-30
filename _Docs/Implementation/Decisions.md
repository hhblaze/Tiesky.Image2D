# Technical decision log

## 2026-08-29 — narrow one-shot API

Expose only encoded-input transforms and encoder option objects. Keeping pixels internal allows storage, fusion, and ISA strategy to change without breaking consumers.

## 2026-08-29 — independent codecs

Use specification-driven implementations and no production package references. Local ImageSharp binaries are permitted only as test oracle and benchmark opponent outside the production tree.

## 2026-08-29 — aligned native RGBA

Use 32-byte-aligned native storage for predictable large-object behavior and vector access. All owners are deterministic `IDisposable` scopes; no finalizer is relied on.

## 2026-08-29 — fused coordinate mapping

Compose EXIF and user rotation into sampling coordinates. Choose the separable pass order by intermediate pixel count, avoiding a full-size oriented copy for resize workloads.

## 2026-08-29 — premultiplied filtering

Perform resize accumulation in premultiplied alpha and return straight RGBA. This is slightly more arithmetic than independent channels but avoids invalid transparent-edge color.

## 2026-08-29 — exact ISA primitives

Restrict explicit SIMD to byte-exact copy/reversal operations initially. This guarantees scalar/ISA identity; future vectorized filtering must retain identical fixed-point arithmetic or introduce a separately documented compatibility decision.

## 2026-08-29 — performance gate remains blocking

Keep benchmark thresholds executable and record failures rather than weakening them. Full-resolution JPEG plane construction is the current bottleneck; a future decoder should perform IDCT scaling and feed resize rows without materializing the full RGBA image.

## 2026-08-30 — quality-bounded native JPEG reduction

Pass requested geometry into the JPEG decoder and select power-of-two reduced IDCT only for baseline, non-CMYK thumbnails with at least 25% source-sampling margin. Preserve original logical dimensions alongside reduced storage dimensions. This removes the full-size component/RGBA allocation while keeping the public resize contract independent of the decoder fast path.

## 2026-08-30 — benchmark gates remain honest

The tracked high-frequency synthetic fixture exposes a final-output PSNR failure at 800 and 1600 pixels, including a sub-38 dB result on the pre-fast-path full decode. Keep the 38 dB executable gate and report failure; do not weaken it merely to publish a green performance result. Latency work and output-equivalence work are separate remaining gates.

## 2026-08-30 — proven opacity is shared decoder state

Allow every decoder to classify opacity, but require either a format-level proof or a complete alpha scan. This enables the existing opaque resize accumulator for RGB PNG, non-alpha BMP, VP8 without `ALPH`, and opaque VP8L while making it impossible for a transparent input to enter that path.

## 2026-08-30 — exact codec SIMD with scalar tails

Use SIMD only where integer byte results are identical: PNG `Up` and RGB8 Paeth, BMP BGR shuffles, VP8L channel shuffles, copy, and pixel reversal. Keep prediction, entropy, and floating resize semantics common. A forced internal dispatch lets complete fixtures prove scalar/SSSE3/AVX2 route identity without expanding the public API.

## 2026-08-30 — non-JPEG performance is reported, not gated

Retain `--gate` for optional investigation, but do not make the non-JPEG delivery conditional on matching a multi-thread-capable reference implementation. Correct dimensions, alpha, lossless corpus pixels, stable failures, and perceptual VP8 thresholds remain blocking. Publish all latency and memory regressions or gains in the self-contained HTML report.

## 2026-08-30 — JPEG report rows use a self-baseline

Include the established JPEG-to-JPEG thumbnail matrix in the combined codec report. Treat each final JPEG result as its own baseline so baseline/final latency, stage, and memory ratios remain exactly 1.00x; the meaningful JPEG performance comparison is the isolated ImageSharp 3.1.12 result from the same run. Preserve the historical baseline for every non-JPEG and encoder-only row.

## 2026-08-30 — opaque PNG owns RGB24

Store proven-opaque, non-interlaced RGB8 PNG directly as aligned RGB24. Preserve RGB24 through identity and discrete rotation, consume it directly in the first resize pass and both encoders, and never classify an explicit-alpha or tRNS input from partial evidence. The storage choice remains internal so public contracts do not change.

## 2026-08-30 — segmented IDAT and deterministic fixed Paeth

Expose validated IDAT payloads to zlib as one pinned forward-only stream instead of concatenating them. Decode RGB Paeth through three exact SIMD dependency lanes. Encode color type 2/6 with fixed Paeth: on the tracked corpus it materially reduces work and output size versus the former five-candidate search while remaining deterministic. Uncommon PNG layouts keep the specification-complete generic fallback.

## 2026-08-30 — retained adaptive parallel thresholds

Parallelize only independent destination tile/row work at measured thresholds: rotation at 1 MP, resize at four million taps, and PNG filtering at 4 MiB. Bound each operation by the logical processor count and preserve the per-pixel arithmetic order. The formal matrix showed no non-PNG latency or memory regression above 5%; forced-serial and automatic results are byte-identical.

## 2026-08-30 — PNG parity is a blocking component gate

Add decode-only, predecoded R90, and 12 MP level-6 encode workloads to the existing six PNG thumbnails. `--gate png-parity` requires every PNG row to remain at or below 1.05x ImageSharp median latency and 1.10x peak private bytes, with dimensions, quality, and baseline output-size checks. Keep other gate modes available for broader investigation.

## 2026-08-30 — bounded ordered JPEG pipelines

Separate baseline entropy parsing from independent block transforms. Overlap one ordered producer with one bounded IDCT batch and keep predictors/restart handling on the producer. On encode, parallelize MCU preprocessing and AC word preparation but retain DC prediction and byte stuffing in source order. Cap both coefficient slabs near 1 MiB and encoder preprocessing at four workers: this preserved parity at 1600 px while bringing the JPEG memory gate below 1.10 and the VP8→JPEG memory change back within 5% of its captured baseline.

## 2026-08-30 — JPEG owns RGB24 and exact quarter-phase SIMD

Treat every decoded JPEG as opaque and write common gray/YCbCr output directly to RGB24. Fuse Cb/Cr centered upsampling coordinate work, use the same integer quarter-phase reconstruction in all paths, and vectorize only the final fixed-point channel conversion and RGB packing. Keep generic sampling, RGB, CMYK, and YCCK fallbacks. Exact output hashes and forced scalar/ISA tests are blocking.

## 2026-08-30 — JPEG parity has component evidence

Add isolated 12 MP decode, predecoded R90, predecoded 1600 px resize, and 12 MP quality-85 encode rows to the six JPEG transforms. `--gate jpeg-parity` applies the 1.05 latency and 1.10 peak-private limits to all four components and both 1600 px transforms, while preserving dimensions, baseline quality, and output-size bounds. Main JPEG transform rows retain the documented 1.00x self-baseline; a separate table records actual pre-optimization improvements.

## 2026-08-30 — VP8L uses two-level canonical tables and exact transform SIMD

Replace per-symbol Huffman tree walking with a validated root/secondary canonical table capped at a 10-bit root. Preserve a bounded tree fallback for uncommon long codes and strict incomplete/oversubscribed validation. Retain only exact integer vector work: subtract-green, color-transform bands, predictor-11 selection, final channel packing, and alpha scans. Proven-opaque VP8L becomes RGB24 only after a header proof or complete final scan.

## 2026-08-30 — VP8 overlaps ordered tokens with ordered reconstruction

Keep arithmetic token parsing and its above/left coefficient contexts on one producer. Alternate two bounded macroblock-row coefficient slabs and reconstruct the preceding row on one consumer, preserving raster prediction dependencies while removing reconstruction from the latency-critical path. Apply the pipeline only from 1 MP and route it through `ParallelExecution` so forced-serial equivalence and server concurrency remain testable.

## 2026-08-30 — VP8 planes are deterministic native owners

Allocate padded Y/U/V reconstruction planes with 32-byte-aligned native ownership and release them before returning RGB24/RGBA32. This avoids retaining three large managed-heap segments in predecoded output-encode workers and brings VP8→JPEG peak private bytes within the blocking limit. Keep alpha managed because its lifetime and decoder vary independently; it is absent for the primary opaque workload.

## 2026-08-30 — WebP parity is a blocking baseline-preserving gate

Add four 12 MP components for each WebP codec and apply `--gate webp-parity` to those eight rows plus all twelve WebP transformations. Require 1.05× ImageSharp latency and 1.10× peak private bytes while comparing dimensions, output size, and quality against the captured pre-stage row. VP8 is intentionally judged by preserved perceptual/golden output rather than exact equality with ImageSharp; VP8L remains exactly equal to the ImageSharp RGBA oracle.

## 2026-08-30 — resize intermediates remain RGBA-aligned

Keep compact RGB24 for decoded, rotated, and final resized opaque images, but widen the bounded separable intermediate to four-byte rows with implicit alpha 255. A full isolated PNG repeat showed that packed three-byte intermediates regressed the 200 px R0 workload beyond the 5% shared-codec limit. Four-byte rows restore aligned convolution while retaining compact output and the WebP memory gate.

## 2026-08-30 — ordered built-in transformations with a separate encoder

Add `IImageTransformation` as a built-in-only operation union for rotation and resize. Keep encoding as a mandatory method parameter because every transform must terminate in a known encoded representation. Snapshot mutable options, compose only adjacent rotations, and preserve the established fused first phase and decoder-native JPEG reduction. Retain `TransformOptions` as an allocation-conscious convenience adapter rather than forcing existing calls through interface objects.

## 2026-08-30 — explicit output ownership contracts

Expose explicit byte-array, returned-memory-stream, caller-stream, and `IBufferWriter<byte>` results for byte-array, span, and stream inputs. Rewind only library-owned returned streams; never reposition caller destinations. Use `IBufferWriter<byte>` rather than a fixed output span because encoded length cannot be known safely in advance.

## 2026-08-30 — header-only public identification

Expose immutable format, MIME, encoded geometry, visual EXIF-oriented geometry, orientation, and animation state. Parse only decisive bounded headers and do not imply full payload integrity. Restore seekable input positions on both success and failure; allow non-seekable inputs to advance by the inspected prefix while leaving them open.

## 2026-08-30 — benchmark HTML preserves its embedded baseline

Allow render mode to read the `Final` object from an existing self-contained HTML report, while baseline loading continues to read its `Baseline` object. Reproduction passes the tracked report as `--baseline` before overwriting it, retaining historical comparison tables across architecture-only reruns without repeating measurements merely to re-render them.
