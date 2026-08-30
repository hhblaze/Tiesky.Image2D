# Architecture

## Pipeline

`ImageTransformer` validates resource limits, buffers a stream input through `ArrayPool<byte>`, detects the format by signature, decodes one static frame to aligned RGB24 or RGBA32, applies EXIF orientation followed by an ordered list of rotations/resizes, and encodes PNG or baseline JPEG through a separate required encoder. JPEG and proven-opaque non-interlaced RGB8 PNG stay RGB24 through identity/rotation and direct encoding; alpha-bearing and generic codec paths stay RGBA32. Caller-owned streams are never disposed.

The public API exposes no pixel buffers or unsafe types. Built-in operation descriptions are snapshotted and compiled into fused rotation/resize phases; unknown external `IImageTransformation` implementations are rejected. The original `TransformOptions` contract compiles to the same core. `Image2DException.ErrorCode` is the stable machine-readable failure surface; codec-specific details stay in the message.

`ImageInspector` uses incremental JPEG, PNG, BMP, VP8, VP8L, and VP8X header readers. It reports encoded and EXIF-oriented geometry without allocating a pixel buffer or running entropy/decompression work. Seekable streams are restored; non-seekable streams consume a bounded header prefix.

## Dependency boundary

Production targets `net8.0`, has no `PackageReference`, and uses only the BCL, internal unsafe code, and `System.Runtime.Intrinsics`. ImageSharp is referenced directly from the local NuGet cache only by the repository's oracle and benchmark projects.

## Execution model

One transform is synchronous to its caller. Internally, independent work becomes adaptively parallel at retained thresholds: rotation at 1 MP, resize at four million estimated tap operations, PNG filtering at 4 MiB of scanline data, JPEG baseline IDCT at two million pixel-block work units, JPEG encoding at 1 MP, and the VP8 ordered-token/reconstruction pipeline at 1 MP. Workers are bounded by `Environment.ProcessorCount`; JPEG encoding and WebP output conversion additionally cap independent work at four workers to control server memory and 32-call throughput. Smaller requests remain serial. Codec state, scratch spans, Huffman tables, and native owners are invocation-local; immutable standard tables are shared. `ParallelExecution.ForceSequential` is internal and exists only for exact equivalence tests.

## Transform fusion

`CoordinateTransform` composes EXIF orientation and a user rotation as an output-to-source coordinate map. Resize consumes that map directly, so no full-size rotated precursor is allocated. The ordered pipeline combines adjacent rotations and groups each pending rotation with the immediately following resize. Repeated or resize-before-rotation sequences hand ownership to another bounded phase. The processor compares `targetWidth × orientedHeight` with `orientedWidth × targetHeight` and chooses the smaller separable intermediate.

Rotation-only R90/R180/R270 requests use 32×32 cache tiles. Large tile-row ranges execute independently and write disjoint destination regions. RGB24 remains packed across rotation and final resize output. Resize intermediates use aligned RGBA32 rows because the 25% byte reduction of RGB24 intermediates regressed small-target convolution latency; alpha is an implicit 255 for the opaque path. Transparent input stays RGBA32 and retains premultiplied-alpha filtering.

Normal-orientation rows bypass the general coordinate switch. When the two possible intermediates have equal size, 90-degree rotations use a vertical-first traversal so filter taps stay contiguous in raw rows. This changes only the separable pass order and never allocates a rotated source image.

Each decoder attaches a proven opacity classification to its owned pixels. Header/mask facts are used where sufficient; explicit alpha formats are scanned completely. The shared resize path may use its opaque RGB accumulator only when that proof is true. Transparent images always retain premultiplied filtering.

For baseline JPEG thumbnails the decoder receives an internal, immutable description of the requested rotation and resize. It selects a native 1/2, 1/4, or 1/8 IDCT reduction only when the reduced crop retains a 25% sampling margin. `DecodedImage` keeps both reduced pixel geometry and original logical geometry, so EXIF, `Contain`/`Cover`, odd source dimensions, and `AllowUpscale` continue to use the public source dimensions.

Full baseline JPEG entropy remains strictly ordered, including DC predictors and restart boundaries. It fills alternating bounded coefficient slabs while the preceding slab performs independent IDCT work, then converts common 4:4:4/4:2:2/4:2:0 YCbCr layouts directly into RGB24. JPEG encoding performs RGB conversion, sampling, DCT, quantization, and AC bit preparation in bounded parallel batches; DC prediction and byte-stuffed Huffman emission remain sequential in MCU order, preserving deterministic output.

VP8 arithmetic tokens remain strictly ordered. For images at or above 1 MP, the producer decodes one macroblock row into either of two bounded coefficient slabs while a single consumer reconstructs the preceding row in raster order. This preserves above/left prediction dependencies and exact coefficient contexts while overlapping entropy and reconstruction. Smaller frames use the identical row representation synchronously.

## Extension checklist

Any new format or transform must preserve checked size arithmetic, maximum-pixel validation before native allocation, caller stream ownership, single-frame behavior, metadata stripping, and deterministic scalar/SIMD output. Update all documents in this directory with the same change.
