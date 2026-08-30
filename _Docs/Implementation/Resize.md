# Resize mathematics

## Geometry

For `Contain`, `scale = min(Wt/Ws, Ht/Hs)`; for `Cover`, `scale = max(Wt/Ws, Ht/Hs)` and a centered source crop is derived; `Stretch` uses independent axes. Unless `AllowUpscale` is true, scale is capped at one. Rounded output dimensions are clamped to at least one pixel.

The sample center mapping is `source = cropStart + (destination + 0.5) × cropExtent / destinationExtent - 0.5`.

## Kernels

- Bilinear: `max(0, 1 - |x|)` with radius 1.
- Bicubic: Keys cubic with `a = -0.5`, radius 2.
- Lanczos3: `sinc(x) × sinc(x/3)` for `|x| < 3`.

When shrinking, kernel support is widened by the reciprocal scale and weights are normalized. Edge indices clamp to the first/last source sample. Kernel indices and normalized weights are precomputed once per axis.

The processor chooses the pass with the smaller RGBA intermediate. Equal-size quarter turns use the vertical logical pass first because its mapped taps are contiguous within raw rows. Normal coordinates cache source rows and spans outside destination-pixel loops. RGB24 sources are consumed without a full-resolution RGBA expansion; the bounded intermediate and final resized image remain RGBA32. Both choices use the same axis maps; only intermediate byte rounding can differ when equal-size pass order changes.

Each separable pass writes independent destination rows. When the combined estimate reaches four million tap operations, those rows execute through a bounded `Parallel.For`; otherwise the same loop body runs serially. The kernel maps and accumulation order within one pixel never change, which makes forced-serial and automatic output byte-identical.

## Alpha

RGB is accumulated after multiplication by straight alpha. The final sample is unpremultiplied and rounded to byte channels; fully transparent output stores zero RGB. This prevents colored halos at transparent edges.

When a JPEG, PNG, BMP, VP8, or VP8L decoder proves that the complete source is opaque, the same normalized kernel weights are applied directly to RGB with `Vector4` accumulation and alpha is stored as 255. This is mathematically equivalent to premultiplied filtering at alpha 1 and avoids per-sample alpha arithmetic. Explicit alpha formats require a complete scan before setting the proof.
