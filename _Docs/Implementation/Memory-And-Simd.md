# Memory ownership and SIMD invariants

## Owners

`PixelBuffer` owns one tightly packed RGB24 or RGBA32 allocation from `NativeMemory.AlignedAlloc(..., 32)`. Its immutable `BytesPerPixel` is restricted to 3 or 4 and participates in checked row/allocation arithmetic. `Dispose` exchanges the pointer with null and frees exactly once. JPEG component planes, progressive coefficient planes, VP8L word images, and VP8 padded Y/U/V planes follow the same scoped-owner pattern. VP8 planes have a finalizer only as construction-failure insurance; normal decoding always releases them deterministically through `FrameState.Dispose`. Every allocation is inside a `try/finally` or `using`; a transformed buffer is disposed separately only when it is not the decoder-owned identity buffer.

Encoded transform-stream input is rented from `ArrayPool<byte>` and cleared only as required by pool policy. Returned `byte[]` and `MemoryStream` outputs are managed and owned by the caller; returned streams are rewound only after successful encoding. Caller-owned output streams remain open and retain their position after the appended image. `IBufferWriter<byte>` output is reached through a non-owning stream adapter that requests and advances destination spans directly, without an encoded-image staging buffer. No native pixel span or pointer escapes a transform call.

Each ordered processing phase returns either the current identity buffer or a new native owner. The pipeline disposes the prior non-decoder buffer immediately after a successful ownership handoff and releases the final buffer in `finally`. Decoder pixels retain exactly one owner throughout. Identification rents only a geometrically growing header prefix for non-seekable streams and never allocates native pixels.

Baseline JPEG decoding rents at most two alternating 1 MiB coefficient slabs; the producer never reuses a slab until its IDCT task completes. Baseline JPEG encoding rents one approximately 1 MiB coefficient slab plus bounded prepared-AC word/count arrays derived from the same MCU batch. Its four-worker cap avoids multiplying worker-stack and pool pressure on server workloads. Every array is returned in `finally`, DC predictors and restart state are never shared with workers, and entropy output has one ordered owner.

VP8 allocates two coefficient rows of `macroblockWidth × 25 × 16` integers and two `macroblockWidth × 25` coefficient-end rows. A row slot is reused only after its reconstruction task completes. The token producer owns boolean readers and coefficient contexts; the reconstruction consumer alone owns Y/U/V writes. The slabs are below 1 MiB for the maximum VP8 width and remain bounded independently of image height.

## Arithmetic

Dimensions are positive and checked against `MaxInputPixels` before pixel allocation. Products, row sizes, chunk boundaries, and native byte counts use checked arithmetic. Parsers distinguish malformed/truncated data from unsupported features using stable error codes.

## SIMD

`SimdPrimitives` contains scalar, SSE2, SSSE3, and AVX2 routes plus an internal forced-mode switch used only by the friend test runner. Forward copies use 256/128-bit exact moves. Reverse RGBA copies use SSSE3 byte shuffle; AVX2 additionally exchanges 128-bit lanes. PNG RGB8 Paeth has scalar, SSSE3, and independently forced AVX2 implementations; automatic dispatch selects the faster 128-bit route because one row contains only three dependency chains. PNG `Up`, BMP BGR conversion, VP8L final channel conversion, and JPEG YCbCr-to-RGB24 conversion also have exact vector routes. JPEG uses 32-bit AVX2 fixed-point channel arithmetic followed by SSSE3 packed RGB stores; the scalar and vector paths use the same constants, rounding, clamping, and quarter-phase samples. Remainders are scalar and no route reads beyond its allocation.

SIMD changes must be bit-identical to scalar for odd and aligned lengths, must not read outside the provided span, and must tolerate an unavailable forced ISA by falling back. The console suite explicitly executes every selector on random rows and complete JPEG, PNG, BMP, VP8L, and VP8 fixtures. Floating-point resize accumulation is deliberately shared rather than duplicated by ISA, preventing hardware-route rounding drift.

JPEG, PNG, BMP, VP8, and VP8L decoded images carry an internal opaque flag. The resize processor may then omit premultiply/unpremultiply work and accumulate RGB in `Vector4`; alpha-bearing inputs retain the straight-alpha/premultiplied path. This flag comes from a format guarantee or a complete alpha scan, never a partial inference. Opaque RGB24 remains three bytes per pixel through decode, rotation, and final resized output. Separable intermediates are aligned RGBA32 with implicit alpha 255 because measured RGB24 intermediate strides regressed small-thumbnail convolution; encoders still consume the final compact RGB24 buffer directly.
