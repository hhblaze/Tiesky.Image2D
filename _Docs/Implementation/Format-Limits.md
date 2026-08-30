# Format limits and behavior

| Format | Decode | Encode | Deliberate exclusions |
|---|---|---|---|
| JPEG | 8-bit baseline/progressive Huffman; gray, YCbCr/RGB, CMYK/YCCK | Baseline, 4:4:4 or 4:2:0 | Arithmetic coding, 12-bit, multi-frame |
| PNG | Static types 0/2/3/4/6, legal 1–16-bit depths, tRNS, Adam7 | Static RGB8/RGBA8 | APNG |
| BMP | Windows INFO/V4/V5, 1/4/8/16/24/32, bitfields, RLE4/8, top-down | No | OS/2, embedded JPEG/PNG, 2-bpp extension |
| WebP | Static VP8, VP8L, VP8X/ALPH | No | Animation |

EXIF orientation is consumed before user rotation and resize. Other metadata is discarded. Samples are treated as sRGB values without ICC conversion. There is no arbitrary-angle rotation, public pixel API, or multi-frame API. Adaptive internal parallelism is an implementation detail and has no public configuration.

`ImageInspector.Identify` recognizes the same four container families without decoding pixels. It reports
APNG/WebP animation even though transformation remains unsupported, and it reports both stored and
EXIF-oriented dimensions. Identification validates decisive headers only; successful identification is
not a full compressed-payload integrity check.

Default guards are 100,000,000 decoded pixels and 512 MiB encoded input. Applications should lower them to match their traffic envelope.
