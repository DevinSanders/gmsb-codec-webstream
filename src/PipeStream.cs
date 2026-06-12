using System;
using System.IO;
using System.Threading;

namespace WebStreamCodecPlugin;

/// <summary>
/// A bounded in-memory pipe — producer thread writes, consumer thread reads,
/// reads block until data is available, writes block when the buffer is full.
///
/// <para><b>Why not <see cref="System.IO.Pipelines.Pipe"/>?</b> Codec
/// decoders take a <see cref="Stream"/>, not a <c>PipeReader</c>. And
/// <see cref="System.IO.Pipelines.PipeReader.AsStream"/> creates a stream
/// that reports <c>CanSeek = false</c> but throws on <c>Position</c> access
/// — and decoders commonly poke at <c>Position</c> early. Hand-rolling a
/// stream that reports CanSeek=false and a no-op Position lets us avoid that
/// throw entirely.</para>
///
/// <para><b>Bounded.</b> The capacity backpressures the network pump: if
/// the audio thread falls behind, the network task blocks on write until
/// space frees up. At a typical 128 kbps stream, 256 KB of buffer is
/// ~16 seconds of audio — plenty to absorb network jitter without
/// noticeable RAM use.</para>
/// </summary>
internal sealed class PipeStream : Stream
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _head;                        // read position
    private int _tail;                        // write position
    private int _count;                       // bytes currently buffered
    private bool _writingComplete;            // producer finished
    private Exception? _fault;                // producer error
    private bool _disposed;

    public PipeStream(int capacity)
    {
        _buffer = new byte[capacity];
    }

    public void CompleteWriting()
    {
        lock (_gate)
        {
            _writingComplete = true;
            Monitor.PulseAll(_gate);
        }
    }

    public void Fault(Exception ex)
    {
        lock (_gate)
        {
            _fault ??= ex;
            _writingComplete = true;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            // Wait for data, EOF, or fault.
            while (_count == 0 && !_writingComplete && !_disposed)
                Monitor.Wait(_gate);

            if (_fault is not null) throw new IOException("Stream source faulted.", _fault);
            if (_count == 0) return 0;        // EOF (writing complete + buffer drained)

            int toCopy = Math.Min(count, _count);
            // The buffer is a ring — a single read may need two memcpys if
            // it wraps around the end. Compute the first contiguous segment.
            int firstSegment = Math.Min(toCopy, _buffer.Length - _head);
            Buffer.BlockCopy(_buffer, _head, buffer, offset, firstSegment);
            int remainder = toCopy - firstSegment;
            if (remainder > 0)
                Buffer.BlockCopy(_buffer, 0, buffer, offset + firstSegment, remainder);

            _head = (_head + toCopy) % _buffer.Length;
            _count -= toCopy;
            // Wake any writers waiting on space.
            Monitor.PulseAll(_gate);
            return toCopy;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            lock (_gate)
            {
                // Wait for space.
                while (_count == _buffer.Length && !_disposed)
                    Monitor.Wait(_gate);

                if (_disposed) return;

                int space = _buffer.Length - _count;
                int toCopy = Math.Min(count - written, space);
                // Ring write — also up to two segments.
                int firstSegment = Math.Min(toCopy, _buffer.Length - _tail);
                Buffer.BlockCopy(buffer, offset + written, _buffer, _tail, firstSegment);
                int remainder = toCopy - firstSegment;
                if (remainder > 0)
                    Buffer.BlockCopy(buffer, offset + written + firstSegment, _buffer, 0, remainder);

                _tail = (_tail + toCopy) % _buffer.Length;
                _count += toCopy;
                written += toCopy;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    // Stream-based decoders probe Length and Position at construction.
    // Returning 0/0 (rather than throwing) lets them fall into streaming
    // mode cleanly.
    public override long Length => 0;
    public override long Position { get => 0; set { /* no-op */ } }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                Monitor.PulseAll(_gate);
            }
        }
        base.Dispose(disposing);
    }
}
