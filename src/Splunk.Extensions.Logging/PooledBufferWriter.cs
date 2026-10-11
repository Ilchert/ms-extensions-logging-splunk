using System.Buffers;

namespace Splunk.Extensions.Logging;

/// <summary>
/// <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}"/>. Disposing hands the written data to the
/// provider's queue as a <see cref="ReadOnlySequence{T}"/>; the provider returns the arrays to the pool once it is sent.
/// </summary>
internal sealed class PooledBufferWriter(SplunkLoggerProvider provider, ArrayPool<byte> pool) : IBufferWriter<byte>, IDisposable
{
    private const int SegmentSize = 2048;

    private BufferSegment? _first;
    private BufferSegment? _last;
    private byte[]? _current;
    private int _written;

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _current.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public void Dispose()
    {
        Seal();
        if (_first is null || _last is null) return;
        provider.Enqueue(new ReadOnlySequence<byte>(_first, 0, _last, _last.Memory.Length));
    }

    private void Ensure(int sizeHint)
    {
        var needed = Math.Max(sizeHint, 1);
        if (_current is not null && _current.Length - _written >= needed) return;

        Seal();
        _current = pool.Rent(Math.Max(needed, SegmentSize));
    }

    private void Seal()
    {
        if (_current is null) return;
        if (_written > 0)
        {
            var segment = new BufferSegment(_current.AsMemory(0, _written));
            if (_last is null) _first = segment;
            else _last.Append(segment);
            _last = segment;
        }
        else
        {
            pool.Return(_current);
        }
        _current = null;
        _written = 0;
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void Append(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
