﻿using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ThumbHash;

internal readonly ref struct SpanOwner<T>
{
    private static ArrayPool<T> DefaultPool
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ArrayPool<T>.Shared;
    }

    private readonly T[] _buffer;
    private readonly int _length;

    public static SpanOwner<T> Empty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(0);
    }

    public Span<T> Span
    {
#if NET6_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref T r0 = ref MemoryMarshal.GetArrayDataReference(_buffer);
            return MemoryMarshal.CreateSpan(ref r0, _length);
        }
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            return new Span<T>(_buffer, 0, _length);
        }
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanOwner<T> WithLength(int length) => new(length, _buffer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanOwner(int length) : this(length, DefaultPool.Rent(length))
    {
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SpanOwner(int length, T[] buffer)
    {
        _length = length;
        _buffer = buffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        DefaultPool.Return(_buffer);
    }
}
