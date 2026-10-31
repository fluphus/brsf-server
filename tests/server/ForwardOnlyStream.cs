// Models Android content-provider streams: seeking and length are unavailable.
internal sealed class ForwardOnlyStream(Stream source, Action? onRead = null) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { onRead?.Invoke(); return source.Read(buffer, offset, Math.Min(count, 8192)); }
    public override int Read(Span<byte> buffer) { onRead?.Invoke(); return source.Read(buffer[..Math.Min(buffer.Length, 8192)]); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { onRead?.Invoke(); return source.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], token); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long length) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
