namespace Xsync;

public sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onProgress;
    private readonly long _checkpointInterval;
    private long _totalRead;
    private long _lastCheckpoint;

    public ProgressStream(Stream inner, long checkpointInterval, Action<long> onProgress)
    {
        _inner = inner;
        _checkpointInterval = checkpointInterval;
        _onProgress = onProgress;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _totalRead += read;
            if (_totalRead - _lastCheckpoint >= _checkpointInterval)
            {
                _onProgress(_totalRead);
                _lastCheckpoint = _totalRead;
            }
        }

        return read;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
