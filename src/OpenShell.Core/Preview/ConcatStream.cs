namespace OpenShell.Preview;

/// <summary>
/// 串联两个流的只读 Stream。用于在非 seekable 流上预读前 8KB 做二进制检测后,
/// 仍能将预读字节喂给后续 <see cref="System.IO.StreamReader"/>。
/// 仅供预览/搜索内部使用。
/// </summary>
internal sealed class ConcatStream : Stream
{
    private readonly Stream _first;
    private readonly Stream _second;
    private bool _firstDone;

    public ConcatStream(Stream first, Stream second)
    {
        _first = first;
        _second = second;
        _firstDone = false;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_firstDone)
        {
            var n = _first.Read(buffer, offset, count);
            if (n > 0) return n;
            _firstDone = true;
        }
        return _second.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_firstDone)
        {
            var n = await _first.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0) return n;
            _firstDone = true;
        }
        return await _second.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _first.Dispose();
            _second.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _first.DisposeAsync().ConfigureAwait(false);
        await _second.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
