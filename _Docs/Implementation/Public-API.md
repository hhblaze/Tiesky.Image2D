# Public pipeline and information API

## Ordered operations

`IImageTransformation` is a closed v1 configuration union. The production pipeline accepts only
`RotateTransformation` and `ResizeTransformation`; an external implementation receives the stable
`InvalidOptions` error. Keeping pixels private preserves the ability to change RGB/RGBA storage,
fusion, SIMD, and native ownership without breaking consumers.

Encoding is deliberately separate from the operation list and is mandatory on every new overload.
The parameter order is `input, transformations, encoder, readOptions` for returned results and
`input, destination, transformations, encoder, readOptions` for caller-owned destinations. An empty
list is a valid transcode. The list, resize settings, encoder settings, and read limits are copied at
entry so later mutations do not affect an active call.

Automatic EXIF orientation is the implicit first operation. Public rotations and resizes then execute
in list order. Adjacent rotations are composed. A rotation immediately followed by a resize uses the
existing fused coordinate map, while repeated or differently ordered resizes become separate native
buffer phases. Only pixel-equivalent adjacent operations may be fused.

The original `TransformOptions` methods are retained and compile directly to one rotation/resize
phase plus the separate encoder. They do not pass through public operation objects and therefore add
no per-operation interface dispatch to established benchmark workloads.

## Input and output matrix

Every output form accepts explicit `byte[]`, `ReadOnlySpan<byte>`, and `Stream` input:

| Result | Contract | Ownership |
|---|---|---|
| Managed bytes | `byte[] Transform(...)` | Returned array belongs to the caller. |
| Encoded stream | `MemoryStream TransformToStream(...)` | Returned stream belongs to the caller and starts at position zero. |
| Stream sink | `void Transform(..., Stream, ...)` | Destination stays open; bytes append at its current position. |
| Buffer sink | `void Transform(..., IBufferWriter<byte>, ...)` | Writer stays caller-owned; encoders advance it directly. |

`Span<byte>` converts to `ReadOnlySpan<byte>` for input. A fixed output span is intentionally absent
because encoded length is unknown; `ArrayBufferWriter<byte>.WrittenSpan` is the safe span-oriented
output. The internal `BufferWriterStream` does not own or complete the writer and adds no encoded-image
staging buffer.

Stream input is consumed from its current position and stays open. It is still pooled into one bounded
contiguous buffer because the current decoders operate on spans. Output streams are never repositioned.

## Identification

`ImageInspector.Identify` accepts `byte[]`, `ReadOnlySpan<byte>`, or `Stream` and returns immutable
`ImageInfo`. `ImageFormat` distinguishes JPEG, PNG, BMP, and WebP; `MimeType` uses `image/jpeg`,
`image/png`, `image/bmp`, or `image/webp`.

`EncodedWidth` and `EncodedHeight` describe the stored pixel grid. `Width` and `Height` describe the
visual grid after EXIF orientation, so orientations 5–8 exchange the axes. `ExifOrientation` is Normal
for formats without the TIFF tag. `IsAnimated` reports APNG `acTL` and the WebP extended animation flag;
transformation of animated containers remains unsupported.

Identification validates the decisive headers and resource limits but does not decode pixels or prove
the integrity of later compressed payloads. A seekable stream is restored to its original position in
success and failure paths. A non-seekable stream stays open and consumes only the buffered prefix needed
to identify its header.
