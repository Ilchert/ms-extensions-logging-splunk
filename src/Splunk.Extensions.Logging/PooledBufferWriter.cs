using System.Buffers;

namespace Splunk.Extensions.Logging;

/// <summary>
/// <see cref="IBufferWriter{T}"/> backed by rented <see cref="ArrayPool{T}"/> arrays. The written data can be copied to a stream;
/// <see cref="Dispose"/> returns the arrays to the pool.
/// </summary>
internal sealed class PooledBufferWriter(ArrayPool<byte> pool) : IBufferWriter<byte>, IDisposable
{
    private const int SegmentSize = 2048;

    private readonly List<ArraySegment<byte>> _segments = [];
    private byte[]? _current;
    private int _written;

    /// <summary>Number of bytes written so far.</summary>
    public long Length { get; private set; }

    public void Advance(int count)
    {
        _written += count;
        Length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _current.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    /// <summary>Finishes writing. Must be called before <see cref="CopyTo"/> or <see cref="CopyToAsync"/>.</summary>
    public void Complete()
    {
        if (_current is null) return;
        AddSegment(_current, _written);
        _current = null;
        _written = 0;
    }

    public void CopyTo(Stream stream)
    {
        foreach (var s in _segments)
            stream.Write(s);
    }

    public async Task CopyToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        foreach (var s in _segments)
            await stream.WriteAsync(s, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns all rented arrays to the pool. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_current is not null) pool.Return(_current);
        _current = null;
        _written = 0;
        foreach (var s in _segments)
            pool.Return(s.Array!);
        _segments.Clear();
        Length = 0;
    }

    private void Ensure(int sizeHint)
    {
        var needed = Math.Max(sizeHint, 1);
        if (_current is not null && _current.Length - _written >= needed) return;

        if (_current is not null) AddSegment(_current, _written);
        _written = 0;
        _current = pool.Rent(Math.Max(needed, SegmentSize));
    }

    private void AddSegment(byte[] array, int length)
    {
        if (length > 0) _segments.Add(new ArraySegment<byte>(array, 0, length));
        else pool.Return(array);
    }
}