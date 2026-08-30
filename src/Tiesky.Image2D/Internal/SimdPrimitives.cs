using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tiesky.Image2D.Internal;

/// <summary>Selects a deterministic implementation for byte-oriented primitives.</summary>
internal enum SimdMode
{
    Automatic,
    Scalar,
    Sse2,
    Ssse3,
    Avx2,
}

/// <summary>Exact byte primitives with scalar, SSE2/SSSE3, and AVX2 implementations.</summary>
internal static class SimdPrimitives
{
    private static readonly Vector128<byte> ReverseFourPixelsMask = Vector128.Create(
        (byte)12, 13, 14, 15, 8, 9, 10, 11, 4, 5, 6, 7, 0, 1, 2, 3);

    private static readonly Vector256<byte> ReverseEightPixelsMask = Vector256.Create(ReverseFourPixelsMask, ReverseFourPixelsMask);

    /// <summary>Overrides automatic dispatch for deterministic internal parity tests.</summary>
    internal static SimdMode ForcedMode { get; set; } = SimdMode.Automatic;

    /// <summary>Gets whether portable vector-byte operations may run.</summary>
    internal static bool AllowPortableVector => ForcedMode != SimdMode.Scalar;

    /// <summary>Gets whether SSSE3 byte shuffles may run for the selected mode.</summary>
    internal static bool AllowSsse3 => Ssse3.IsSupported && ForcedMode is SimdMode.Automatic or SimdMode.Ssse3 or SimdMode.Avx2;

    /// <summary>Gets whether AVX2 integer operations may run for the selected mode.</summary>
    internal static bool AllowAvx2 => Avx2.IsSupported && ForcedMode is SimdMode.Automatic or SimdMode.Avx2;

    /// <summary>Copies bytes using the widest requested implementation supported by the CPU.</summary>
    public static void Copy(ReadOnlySpan<byte> source, Span<byte> destination, SimdMode mode = SimdMode.Automatic)
    {
        if (mode == SimdMode.Automatic && ForcedMode != SimdMode.Automatic) mode = ForcedMode;
        if (destination.Length < source.Length)
        {
            ThrowHelper.InvalidData("The SIMD copy destination is too small.");
        }

        ref byte input = ref Unsafe.AsRef(in source.GetPinnableReference());
        ref byte output = ref destination.GetPinnableReference();
        int offset = 0;
        if ((mode is SimdMode.Automatic or SimdMode.Avx2) && Avx2.IsSupported)
        {
            for (; offset <= source.Length - 32; offset += 32)
            {
                Vector256<byte> value = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref input, offset));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, offset), value);
            }
        }

        if ((mode is SimdMode.Automatic or SimdMode.Sse2 or SimdMode.Ssse3 or SimdMode.Avx2) && Sse2.IsSupported)
        {
            for (; offset <= source.Length - 16; offset += 16)
            {
                Vector128<byte> value = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref input, offset));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, offset), value);
            }
        }

        source[offset..].CopyTo(destination[offset..]);
    }

    /// <summary>Copies RGBA pixels in reverse pixel order without changing channel order.</summary>
    public static void ReversePixels(ReadOnlySpan<byte> source, Span<byte> destination, SimdMode mode = SimdMode.Automatic)
    {
        if (mode == SimdMode.Automatic && ForcedMode != SimdMode.Automatic) mode = ForcedMode;
        if ((source.Length & 3) != 0 || destination.Length < source.Length)
        {
            ThrowHelper.InvalidData("RGBA reverse spans are inconsistent.");
        }

        ref byte input = ref Unsafe.AsRef(in source.GetPinnableReference());
        ref byte output = ref destination.GetPinnableReference();
        int written = 0;
        if ((mode is SimdMode.Automatic or SimdMode.Avx2) && Avx2.IsSupported)
        {
            for (; written <= source.Length - 32; written += 32)
            {
                int inputOffset = source.Length - written - 32;
                Vector256<byte> value = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref input, inputOffset));
                value = Avx2.Shuffle(value, ReverseEightPixelsMask);
                value = Avx2.Permute2x128(value, value, 0x01);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, written), value);
            }
        }

        if ((mode is SimdMode.Automatic or SimdMode.Ssse3 or SimdMode.Avx2) && Ssse3.IsSupported)
        {
            for (; written <= source.Length - 16; written += 16)
            {
                int inputOffset = source.Length - written - 16;
                Vector128<byte> value = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref input, inputOffset));
                value = Ssse3.Shuffle(value, ReverseFourPixelsMask);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, written), value);
            }
        }

        for (; written < source.Length; written += 4)
        {
            int inputOffset = source.Length - written - 4;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, written), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref input, inputOffset)));
        }
    }
}
