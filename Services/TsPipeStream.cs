namespace DvbTv.Services;

/// <summary>
/// Thread-safe live pipe between the BDA sample-grabber (producer) and the VLC
/// reader (consumer). The grabber delivers the TS in big bursts (~235 KB); a pull
/// model lets VLC drain at its own pace from this ring buffer, so bursts are
/// absorbed instead of dropped (the failure mode of UDP push).
///
/// Live semantics: if the consumer falls far behind and the ring fills, the
/// OLDEST bytes are discarded (stay live) — the producer (DirectShow thread)
/// never blocks. Read() blocks until data is available or the stream is closed.
/// </summary>
public sealed class TsPipeStream : Stream
{
    private readonly byte[] _buf;
    private readonly object _lock = new();
    private int _readPos;
    private int _count;
    private volatile bool _closed;

    public TsPipeStream(int capacityBytes = 4 * 1024 * 1024) => _buf = new byte[capacityBytes];

    /// <summary>Bytes currently buffered (for underrun/overflow diagnostics).</summary>
    public int Available { get { lock (_lock) return _count; } }
    /// <summary>Total ring capacity in bytes.</summary>
    public int Capacity => _buf.Length;

    /// <summary>Drop all buffered data (called on channel change so VLC doesn't see a mix of old+new mux).</summary>
    public void Clear()
    {
        lock (_lock) { _readPos = 0; _count = 0; }
    }

    /// <summary>Producer: append a TS buffer (drops oldest data if full — never blocks).</summary>
    public void Append(ReadOnlySpan<byte> src)
    {
        if (src.Length == 0) return;
        lock (_lock)
        {
            // If it can't fit, drop the oldest bytes to make room (live: prefer fresh).
            if (src.Length >= _buf.Length)
            {
                src = src[^_buf.Length..];
                _readPos = 0;
                _count = 0;
            }
            else if (_count + src.Length > _buf.Length)
            {
                int overflow = _count + src.Length - _buf.Length;
                _readPos = (_readPos + overflow) % _buf.Length;
                _count -= overflow;
            }

            int writePos = (_readPos + _count) % _buf.Length;
            int firstChunk = Math.Min(src.Length, _buf.Length - writePos);
            src[..firstChunk].CopyTo(_buf.AsSpan(writePos));
            if (firstChunk < src.Length)
                src[firstChunk..].CopyTo(_buf.AsSpan(0));
            _count += src.Length;

            Monitor.PulseAll(_lock);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int waitedMs = 0;
            while (_count == 0 && !_closed && waitedMs < 3000)
            {
                Monitor.Wait(_lock, 500);
                waitedMs += 500;
            }
            if (_count == 0) return 0; // closed, drained, or idle (grabber stopped) → EOF, never block forever

            int n = Math.Min(count, _count);
            int firstChunk = Math.Min(n, _buf.Length - _readPos);
            Array.Copy(_buf, _readPos, buffer, offset, firstChunk);
            if (firstChunk < n)
                Array.Copy(_buf, 0, buffer, offset + firstChunk, n - firstChunk);
            _readPos = (_readPos + n) % _buf.Length;
            _count -= n;
            return n;
        }
    }

    /// <summary>Non-blocking read for the scanner: returns however many bytes are available now (0 if empty).</summary>
    public int ReadAvailable(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_count == 0) return 0;
            int n = Math.Min(count, _count);
            int firstChunk = Math.Min(n, _buf.Length - _readPos);
            Array.Copy(_buf, _readPos, buffer, offset, firstChunk);
            if (firstChunk < n)
                Array.Copy(_buf, 0, buffer, offset + firstChunk, n - firstChunk);
            _readPos = (_readPos + n) % _buf.Length;
            _count -= n;
            return n;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        lock (_lock) Monitor.PulseAll(_lock);
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
