# Codec design

## JPEG

The decoder parses marker segments without retaining metadata other than EXIF orientation. It supports 8-bit sequential and progressive Huffman scans, restart markers, grayscale, YCbCr/RGB, CMYK, and Adobe YCCK. Baseline entropy uses a 64-bit reservoir, four-byte marker-safe refill, 12-bit canonical lookahead, and fused common symbol/amplitude decoding. Ordered entropy fills alternating 1 MiB coefficient slabs while independent dequantization/IDCT work consumes the preceding slab; progressive scans perform parallel final IDCT after reconstruction. Thumbnail requests use reduced fixed-point IDCTs that discard frequencies above the reduced block's Nyquist limit; progressive and four-component inputs retain the full path.

Common 4:4:4, 4:2:2, and 4:2:0 YCbCr layouts write RGB24 directly. Centered quarter-phase chroma reconstruction shares Cb/Cr coordinate work, and AVX2 integer color arithmetic plus SSSE3 RGB packing is bit-identical to the scalar path. Generic RGB, unusual sampling, CMYK, and YCCK retain specification-complete fallbacks.

The encoder writes JFIF baseline JPEG with standard Huffman tables, quality-scaled quantization, 4:4:4 or 4:2:0 sampling, and configurable alpha flattening. Its 4:2:0 RGB24/RGBA32 paths convert each 16×16 MCU once and use an integer AAN DCT with reciprocal quantizers. Bounded 1 MiB coefficient batches prepare color conversion, downsampling, DCT, quantization, zero runs, and packed AC Huffman words with at most four workers; ordered DC prediction and one buffered byte-stuffing writer retain deterministic output.

Normative reference: ITU-T T.81.

## PNG

The decoder validates CRC and ordering and exposes validated IDAT payloads through one pinned, forward-only segmented stream, avoiding a concatenated compressed copy. It handles color types 0, 2, 3, 4, and 6 at every legal 1–16-bit depth. Palette/tRNS and seven Adam7 passes retain the exact generic RGBA fallback. Proven-opaque non-interlaced RGB8 rows are reversed directly into RGB24. Paeth evaluates the three independent channel chains in exact 16-bit SSSE3 lanes; a separate forced AVX2 route and scalar route provide equivalence coverage and scalar tails. APNG `acTL` is rejected.

The encoder emits color type 2 for proven-opaque output and type 6 for alpha-bearing output. It uses a deterministic fixed-Paeth filter and preserves the public compression-level mapping to BCL zlib. At 4 MiB of direct scanline data, bounded 4 MiB row batches are filtered in parallel and written to zlib in source order; smaller images and opaque RGBA packing stay serial. Filter and chunk buffers come from `ArrayPool<byte>`, and encoded output remains deterministic between serial and parallel paths.

Normative reference: PNG Third Edition.

## BMP

Windows INFO/V4/V5 headers are supported with 1/4/8-bit palettes, 16/24/32-bit direct pixels, RGB/ALPHA BITFIELDS, RLE4/RLE8 including delta commands, and top-down storage. Bit-depth dispatch occurs once per row instead of once per pixel. 24/32-bit BGR conversion uses exact SSSE3 shuffles with scalar tails, palette colors are packed for direct 32-bit lookup, encoded RLE runs are bulk-filled, and validated bitfield shift/width data is precomputed once. Narrow bitfields expand by repeating significant bits. Embedded JPEG/PNG, OS/2 headers, and the non-v1 2-bpp palette extension are rejected.

Normative reference: Microsoft bitmap storage documentation.

## WebP

RIFF parsing recognizes static VP8, VP8L, and VP8X/ALPH layouts and rejects animation. VP8L implements transforms, canonical Huffman groups, color cache, and backward references. Validated two-level canonical tables use up to a 10-bit root and bounded secondary lookup, with constant-symbol shortcuts and a tree fallback for uncommon long codes. A pointer-based 64-bit reservoir refills eight guarded bytes at a time. The common single-group loop avoids per-pixel coordinate division, LZ copies are overlap-safe, and inverse subtract-green/color transforms use exact AVX2/SSSE3 arithmetic where independent. Predictor mode 11 uses an exact SSE2 select sum while dependency-bearing predictors remain ordered. Final opacity detection emits RGB24 when every alpha byte is 255 and RGBA32 otherwise.

VP8 implements key-frame boolean partitions, segmentation, intra prediction, coefficient reconstruction, YUV conversion, and deblocking. Its guarded 56-bit boolean reservoir bulk-refills from a pointer and preserves strict truncated-symbol errors. Probability-band offsets and coefficient-end positions are precomputed; EOB/zero/DC-only blocks bypass unnecessary probability and inverse-DCT scans. At 1 MP, ordered token parsing alternates two macroblock-row coefficient slabs while a dependency-safe consumer reconstructs the preceding row. Padded Y/U/V planes use zeroed 32-byte-aligned native owners and are released before the decoded RGB24/RGBA32 buffer escapes. Vertical/horizontal block predictors are specialized, loop-filter traversal is row-local, and bounded parallel output shares one chroma pair across two luma samples. ALPH accepts raw or headerless VP8L compression and filters 0–3. VP8 without `ALPH` is proven opaque. BMP and WebP are decode-only.

Normative references: RFC 9649, RFC 6386, and the WebP lossless bitstream specification.
