# Tiesky.Image2D examples

The console project demonstrates byte arrays, spans, returned streams, caller-owned streams,
`IBufferWriter<byte>`, image identification, ordered transformations, and the original
`TransformOptions` convenience API.

```powershell
dotnet run --project examples\Tiesky.Image2D.Examples -c Release -- <input-image> [output-directory]
```

The input may be JPEG, PNG, BMP, or static WebP. The example writes PNG and JPEG results to
the selected output directory; it does not modify the input file.
