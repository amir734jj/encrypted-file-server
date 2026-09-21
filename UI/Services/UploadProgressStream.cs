namespace UI.Services;

public sealed class UploadProgressStream(
    Stream inner,
    long totalBytes,
    Action<long, int> progress) : Stream
{
    private long _bytesRead;
    private int _lastPercentage = -1;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => totalBytes;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = inner.Read(buffer, offset, count);
        Report(bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var bytesRead = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        Report(bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var bytesRead = await inner.ReadAsync(buffer, cancellationToken);
        Report(bytesRead);
        return bytesRead;
    }

    private void Report(int bytesRead)
    {
        if (bytesRead > 0)
        {
            _bytesRead += bytesRead;
        }

        var percentage = totalBytes == 0
            ? 100
            : (int)Math.Min(100, _bytesRead * 100 / totalBytes);
        if (percentage == _lastPercentage)
        {
            return;
        }

        _lastPercentage = percentage;
        progress(_bytesRead, percentage);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
