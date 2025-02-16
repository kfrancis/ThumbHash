﻿
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ThumbHash;

public static class ThumbHash
{
    private readonly ref struct Channel
    {
        public readonly float DC;
        public readonly SpanOwner<float> AC;
        public readonly float Scale;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Channel(float dc, SpanOwner<float> ac, float scale)
        {
            DC = dc;
            AC = ac;
            Scale = scale;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deconstruct(out float dc, out SpanOwner<float> ac, out float scale)
        {
            dc = DC;
            ac = AC;
            scale = Scale;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RGBA
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RGBA(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deconstruct(out byte r, out byte g, out byte b, out byte a)
        {
            r = R;
            g = G;
            b = B;
            a = A;
        }
    }

    private const int MaxHash = 25;
    private const int MinHash = 5;

    private const int MaxRgbaWidth = 100;
    private const int MaxRgbaHeight = 100;

    private const int MaxThumbHashWidth = 32;
    private const int MaxThumbHashHeight = 32;

    #region ThrowHelpers
#if !NET8_0_OR_GREATER
    public static void ThrowIfLessThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        throw new ArgumentOutOfRangeException(paramName, value, $"'{value}' must be greater than or equal to '{other}'.");
    }

    public static void ThrowIfGreaterThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        throw new ArgumentOutOfRangeException(paramName, value, $"'{paramName}' must be less than or equal to '{other}'.");
    }
#endif

    static void ThrowNotEqual<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null, [CallerArgumentExpression(nameof(other))] string? otherName = null)
    {
        throw new ArgumentOutOfRangeException(paramName, value, $"'{paramName}' must be equal to '{other}' ('{otherName}').");
    }
    #endregion

    /// <summary>
    /// Encodes an RGBA image to a ThumbHash.
    /// </summary>
    /// <param name="width">The width of the input image. Must be ≤100px.</param>
    /// <param name="height">The height of the input image. Must be ≤100px.</param>
    /// <param name="rgba">The pixels in the input image, row-by-row. RGB should not be premultiplied by A. Must have `w*h*4` elements.</param>
    /// <returns>Byte array containing the ThumbHash</returns>
    public static byte[] RgbaToThumbHash(int width, int height, ReadOnlySpan<byte> rgba)
    {
        Span<byte> hash = stackalloc byte[MaxHash];
        var bytesWritten = RgbaToThumbHash(hash, width, height, rgba);
        return hash[..bytesWritten].ToArray();
    }

    /// <summary>
    /// Encodes an RGBA image to a ThumbHash.
    /// </summary>
    /// <param name="hash"></param>
    /// <param name="w">The width of the input image. Must be ≤100px.</param>
    /// <param name="h">The height of the input image. Must be ≤100px.</param>
    /// <param name="rgba_bytes">The pixels in the input image, row-by-row. RGB should not be premultiplied by A. Must have `w*h*4` elements.</param>
    /// <returns>Number of bytes written into hash span</returns>
    public static int RgbaToThumbHash(Span<byte> hash, int w, int h, ReadOnlySpan<byte> rgba_bytes)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(hash.Length, MinHash);
#else
        if (hash.Length < MinHash)
        {
            ThrowIfLessThan(hash.Length, MinHash);
        }
#endif

        // Encoding an image larger than 100x100 is slow with no benefit
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfGreaterThan(w, MaxRgbaWidth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(h, MaxRgbaHeight);
#else
        if (w > MaxRgbaWidth)
        {
            ThrowIfGreaterThan(w, MaxRgbaWidth);
        }

        if (h > MaxRgbaHeight)
        {
            ThrowIfGreaterThan(h, MaxRgbaHeight);
        }
#endif

        if (rgba_bytes.Length != w * h * 4)
        {
            ThrowNotEqual(rgba_bytes.Length, w * h * 4);
        }

        // Determine the average color
        var avg_r = 0.0f;
        var avg_g = 0.0f;
        var avg_b = 0.0f;
        var avg_a = 0.0f;

        var rgba = MemoryMarshal.Cast<byte, RGBA>(rgba_bytes);
        foreach (ref readonly var pixel in rgba)
        {
            var alpha = pixel.A / 255.0f;
            avg_b += alpha / 255.0f * pixel.B;
            avg_g += alpha / 255.0f * pixel.G;
            avg_r += alpha / 255.0f * pixel.R;
            avg_a += alpha;
        }

        if (avg_a > 0.0f)
        {
            avg_r /= avg_a;
            avg_g /= avg_a;
            avg_b /= avg_a;
        }

        var has_alpha = avg_a < (w * h);
        var l_limit = has_alpha ? 5 : 7; // Use fewer luminance bits if there's alpha
        var lx = Math.Max((int)(float)Math.Round(l_limit * w / (float)Math.Max(w, h)), 1);
        var ly = Math.Max((int)(float)Math.Round(l_limit * h / (float)Math.Max(w, h)), 1);

        using var l_owner = new SpanOwner<float>(w * h); // l: luminance
        using var p_owner = new SpanOwner<float>(w * h); // p: yellow - blue
        using var q_owner = new SpanOwner<float>(w * h); // q: red - green
        using var a_owner = new SpanOwner<float>(w * h); // a: alpha

        var l = l_owner.Span;
        var p = p_owner.Span;
        var q = q_owner.Span;
        var a = a_owner.Span;

        // Convert the image from RGBA to LPQA (composite atop the average color)
        int j = 0;
        foreach (ref readonly var pixel in rgba)
        {
            var alpha = pixel.A / 255.0f;
            var b = avg_b * (1.0f - alpha) + alpha / 255.0f * pixel.B;
            var g = avg_g * (1.0f - alpha) + alpha / 255.0f * pixel.G;
            var r = avg_r * (1.0f - alpha) + alpha / 255.0f * pixel.R;
            a[j] = alpha;
            q[j] = r - g;
            p[j] = (r + g) / 2.0f - b;
            l[j] = (r + g + b) / 3.0f;
            j += 1;
        }

        // Encode using the DCT into DC (constant) and normalized AC (varying) terms
        Channel encode_channel(ReadOnlySpan<float> channel, int nx, int ny)
        {
            var dc = 0.0f;
            var ac_owner = new SpanOwner<float>(nx * ny);
            var scale = 0.0f;

            Span<float> fx = stackalloc float[w];
            Span<float> ac = ac_owner.Span;
            int n = 0;
            for (int cy = 0; cy < ny; cy++)
            {
                var cx = 0;
                while (cx * ny < nx * (ny - cy))
                {
                    var f = 0.0f;
                    for (int x = 0; x < w; x++)
                    {
                        fx[x] = (float)Math.Cos(Convert.ToSingle(Math.PI) / w * cx * (x + 0.5f));
                    }
                    for (int y = 0; y < h; y++)
                    {
                        var fy = (float)Math.Cos(Convert.ToSingle(Math.PI) / h * cy * (y + 0.5f));
                        for (int x = 0; x < w; x++)
                        {
                            f += channel[x + y * w] * fx[x] * fy;
                        }
                    }
                    f /= w * h;
                    if (cx > 0 || cy > 0)
                    {
                        ac[n++] = f;
                        scale = (float)Math.Max((float)Math.Abs(f), scale);
                    }
                    else
                    {
                        dc = f;
                    }
                    cx += 1;
                }
            }
            ac_owner = ac_owner.WithLength(n);
            ac = ac_owner.Span;

            if (scale > 0.0f)
            {
                foreach (ref var aci in ac)
                {
                    aci = 0.5f + 0.5f / scale * aci;
                }
            }

            return new Channel(dc, ac_owner, scale);
        };

        var (l_dc, l_ac, l_scale) = encode_channel(l, Math.Max(lx, 3), Math.Max(ly, 3));
        var (p_dc, p_ac, p_scale) = encode_channel(p, 3, 3);
        var (q_dc, q_ac, q_scale) = encode_channel(q, 3, 3);
        var (a_dc, a_ac, a_scale) = has_alpha ? encode_channel(a, 5, 5) : new Channel(1.0f, SpanOwner<float>.Empty, 1.0f);

        // Write the constants
        var is_landscape = w > h;
        var header24 = (uint)(float)Math.Round(63.0f * l_dc)
            | (((uint)(float)Math.Round(31.5f + 31.5f * p_dc)) << 6)
            | (((uint)(float)Math.Round(31.5f + 31.5f * q_dc)) << 12)
            | (((uint)(float)Math.Round(31.0f * l_scale)) << 18)
            | (has_alpha ? 1u << 23 : 0);
        var header16 = (ushort)(is_landscape ? ly : lx)
            | (((ushort)(float)Math.Round(63.0f * p_scale)) << 3)
            | (((ushort)(float)Math.Round(63.0f * q_scale)) << 9)
            | (is_landscape ? 1 << 15 : 0);

        int hi = 0;
        hash[hi++] = (byte)header24;
        hash[hi++] = (byte)(header24 >> 8);
        hash[hi++] = (byte)(header24 >> 16);
        hash[hi++] = (byte)header16;
        hash[hi++] = (byte)(header16 >> 8);
        if (has_alpha)
        {
            var fa_dc = (float)Math.Round(15.0f * a_dc);
            var fa_scale = (float)Math.Round(15.0f * a_scale);
            var ia_dc = (byte)fa_dc;
            var ia_scale = (byte)fa_scale;
            hash[hi++] = (byte)(ia_dc | (ia_scale << 4));
        }

        // Write the varying factors
        static void WriteFactor(ReadOnlySpan<float> ac, ref bool is_odd, ref int hi, Span<byte> hash)
        {
            for (int i = 0; i < ac.Length; i++)
            {
                var u = (byte)((float)Math.Round(15.0f * ac[i]));
                if (is_odd)
                {
                    hash[hi - 1] |= (byte)(u << 4);
                }
                else
                {
                    hash[hi++] = u;
                }
                is_odd = !is_odd;
            }
        }

        using (l_ac)
        using (p_ac)
        using (q_ac)
        using (a_ac)
        {
            var is_odd = false;
            WriteFactor(l_ac.Span, ref is_odd, ref hi, hash);
            WriteFactor(p_ac.Span, ref is_odd, ref hi, hash);
            WriteFactor(q_ac.Span, ref is_odd, ref hi, hash);
            if (has_alpha)
            {
                WriteFactor(a_ac.Span, ref is_odd, ref hi, hash);
            }
        }

        return hi;
    }

    /// <summary>
    /// Decodes a ThumbHash to an RGBA image.
    /// </summary>
    /// <returns>Width, height, and unpremultiplied RGBA8 pixels of the rendered ThumbHash.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the input is too short.</exception>
    public static (int w, int h, byte[] rgba) ThumbHashToRgba(ReadOnlySpan<byte> hash)
    {
        using var rgba_owner = new SpanOwner<byte>(MaxThumbHashWidth * MaxThumbHashHeight * 4);
        var rgba = rgba_owner.Span;
        var (w, h) = ThumbHashToRgba(hash, rgba);
        return (w, h, rgba[..(w * h * 4)].ToArray());
    }

    /// <summary>
    /// Decodes a ThumbHash to an RGBA image.
    /// </summary>
    /// <returns>Width, height, and unpremultiplied RGBA8 pixels of the rendered ThumbHash.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the input is too short.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the RGBA span length is less than `w * h * 4` bytes.</exception>
    public static (int w, int h) ThumbHashToRgba(ReadOnlySpan<byte> hash, Span<byte> rgba)
    {
        var ratio = ThumbHashToApproximateAspectRatio(hash);

        // Read the constants
        var header24 = hash[0]
            | (((uint)hash[1]) << 8)
            | (((uint)hash[2]) << 16);
        var header16 = hash[3] | (hash[4] << 8);
        var l_dc = (header24 & 63) / 63.0f;
        var p_dc = ((header24 >> 6) & 63) / 31.5f - 1.0f;
        var q_dc = ((header24 >> 12) & 63) / 31.5f - 1.0f;
        var l_scale = ((header24 >> 18) & 31) / 31.0f;
        var has_alpha = (header24 >> 23) != 0;
        var p_scale = ((header16 >> 3) & 63) / 63.0f;
        var q_scale = ((header16 >> 9) & 63) / 63.0f;
        var is_landscape = (header16 >> 15) != 0;
        var l_max = has_alpha ? 5 : 7;
        var lx = Math.Max(3, is_landscape ? l_max : header16 & 7);
        var ly = Math.Max(3, is_landscape ? header16 & 7 : l_max);
        var (a_dc, a_scale) = has_alpha ? ((hash[5] & 15) / 15.0f, (hash[5] >> 4) / 15.0f) : (1.0f, 1.0f);

        // Read the varying factors (boost saturation by 1.25x to compensate for quantization)
        static SpanOwner<float> decode_channel(ReadOnlySpan<byte> hash, int start, ref int index, int nx, int ny, float scale)
        {
            var ac_owner = new SpanOwner<float>(nx * ny);
            var ac = ac_owner.Span;
            int n = 0;
            for (int cy = 0; cy < ny; cy++)
            {
                for (int cx = cy > 0 ? 0 : 1; cx * ny < nx * (ny - cy); cx++, n++, index++)
                {
                    var data = hash[start + (index >> 1)] >> ((index & 1) << 2);
                    ac[n] = ((data & 15) / 7.5f - 1.0f) * scale;
                }
            }

            return ac_owner.WithLength(n);
        };

        // Decode using the DCT into RGB
        var (w, h) = ratio > 1.0f ? (MaxThumbHashWidth, (int)((float)Math.Round(32.0f / ratio))) : ((int)((float)Math.Round(32.0f * ratio)), MaxThumbHashHeight);
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(rgba.Length, w * h * 4);
#else
        if (rgba.Length < w * h * 4)
        {
            ThrowIfLessThan(rgba.Length, w * h * 4);
        }
#endif

        var ac_start = has_alpha ? 6 : 5;
        var ac_index = 0;

        using (var l_ac_owner = decode_channel(hash, ac_start, ref ac_index, lx, ly, l_scale))
        using (var p_ac_owner = decode_channel(hash, ac_start, ref ac_index, 3, 3, p_scale * 1.25f))
        using (var q_ac_owner = decode_channel(hash, ac_start, ref ac_index, 3, 3, q_scale * 1.25f))
        using (var a_ac_owner = has_alpha ? decode_channel(hash, ac_start, ref ac_index, 5, 5, a_scale) : SpanOwner<float>.Empty)
        {
            var l_ac = l_ac_owner.Span;
            var p_ac = p_ac_owner.Span;
            var q_ac = q_ac_owner.Span;
            var a_ac = a_ac_owner.Span;

            Span<float> fx = stackalloc float[7];
            Span<float> fy = stackalloc float[7];

#if NET6_0_OR_GREATER
            ref RGBA pixel = ref MemoryMarshal.AsRef<RGBA>(rgba);
#else
            var rgbaSpan = MemoryMarshal.Cast<byte, RGBA>(rgba);
            ref RGBA pixel = ref rgbaSpan[0];
#endif
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++, pixel = ref Unsafe.AddByteOffset(ref pixel, (IntPtr)4))
                {
                    var l = l_dc;
                    var p = p_dc;
                    var q = q_dc;
                    var a = a_dc;

                    // Precompute the coefficients
                    for (int cx = 0; cx < Math.Max(lx, has_alpha ? 5 : 3); cx++)
                    {
                        fx[cx] = (float)Math.Cos(Convert.ToSingle(Math.PI) / w * (x + 0.5f) * cx);
                    }
                    for (int cy = 0; cy < Math.Max(ly, has_alpha ? 5 : 3); cy++)
                    {
                        fy[cy] = (float)Math.Cos(Convert.ToSingle(Math.PI) / h * (y + 0.5f) * cy);
                    }

                    // Decode L
                    for (int cy = 0, j = 0; cy < ly; cy++)
                    {
                        var cx = cy > 0 ? 0 : 1;
                        var fy2 = fy[cy] * 2.0f;
                        while (cx * ly < lx * (ly - cy))
                        {
                            l += l_ac[j] * fx[cx] * fy2;
                            j += 1;
                            cx += 1;
                        }
                    }

                    // Decode P and Q
                    for (int cy = 0, j = 0; cy < 3; cy++)
                    {
                        var cx = cy > 0 ? 0 : 1;
                        var fy2 = fy[cy] * 2.0f;
                        while (cx < 3 - cy)
                        {
                            var f = fx[cx] * fy2;
                            p += p_ac[j] * f;
                            q += q_ac[j] * f;
                            j += 1;
                            cx += 1;
                        }
                    }

                    // Decode A
                    if (has_alpha)
                    {
                        for (int cy = 0, j = 0; cy < 5; cy++)
                        {
                            var cx = cy > 0 ? 0 : 1;
                            var fy2 = fy[cy] * 2.0f;
                            while (cx < 5 - cy)
                            {
                                a += a_ac[j] * fx[cx] * fy2;
                                j += 1;
                                cx += 1;
                            }
                        }
                    }

                    // Convert to RGB
                    var b = l - 2.0f / 3.0f * p;
                    var r = (3.0f * l - b + q) / 2.0f;
                    var g = r - q;

                    pixel = new(
                        r: (byte)(r.Clamp(0.0f, 1.0f) * 255.0f),
                        g: (byte)(g.Clamp(0.0f, 1.0f) * 255.0f),
                        b: (byte)(b.Clamp(0.0f, 1.0f) * 255.0f),
                        a: (byte)(a.Clamp(0.0f, 1.0f) * 255.0f));
                }
            }
        }

        return (w, h);
    }

    /// <summary>
    /// Extracts the average color from a ThumbHash.
    /// </summary>
    /// <returns>Unpremultiplied RGBA values where each value ranges from 0 to 1. </returns>
    /// <exception cref="NotImplementedException">Thrown if the input is too short.</exception>
    public static (float r, float g, float b, float a) ThumbHashToAverageRgba(ReadOnlySpan<byte> hash)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(hash.Length, MinHash);
#else
        if (hash.Length < MinHash)
        {
            ThrowIfLessThan(hash.Length, MinHash);
        }
#endif

        var header = hash[0] | ((uint)hash[1] << 8) | ((uint)hash[2] << 16);
        var l = (header & 63) / 63.0f;
        var p = ((header >> 6) & 63) / 31.5f - 1.0f;
        var q = ((header >> 12) & 63) / 31.5f - 1.0f;
        var has_alpha = (header >> 23) != 0;
        var a = has_alpha ? (hash[5] & 15) / 15.0f : 1.0f;
        var b = l - 2.0f / 3.0f * p;
        var r = (3.0f * l - b + q) / 2.0f;
        var g = r - q;

        return (r: r.Clamp(0.0f, 1.0f),
                g: g.Clamp(0.0f, 1.0f),
                b: b.Clamp(0.0f, 1.0f),
                a);
    }

    /// <summary>
    /// Extracts the approximate aspect ratio of the original image.
    /// </summary>
    /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the input is too short.</exception>
    public static float ThumbHashToApproximateAspectRatio(ReadOnlySpan<byte> hash)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(hash.Length, MinHash);
#else
        if (hash.Length < MinHash)
        {
            ThrowIfLessThan(hash.Length, MinHash);
        }
#endif

        var has_alpha = (hash[2] & 0x80) != 0;
        var l_max = has_alpha ? 5 : 7;
        var l_min = hash[3] & 7;
        var is_landscape = (hash[4] & 0x80) != 0;
        var lx = is_landscape ? l_max : l_min;
        var ly = is_landscape ? l_min : l_max;
        return (float)lx / ly;
    }
}

public static class MathExtensions
{
    public static T Clamp<T>(this T val, T min, T max) where T : IComparable<T>
    {
        if (val.CompareTo(min) < 0) return min;
        else if (val.CompareTo(max) > 0) return max;
        else return val;
    }
}
