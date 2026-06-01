using System.Security.Cryptography;
using System.Text;
using Shared.Interfaces;

namespace Api.Services.Encryption;

public sealed class NoneEncryptionProvider : IEncryptionProvider
{
    public string ProviderKey => "none";

    public (Stream encryptingStream, byte[] iv) CreateEncryptingStream(Stream destination, byte[] masterKey)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        return (new PassthroughStream(destination), iv);
    }

    public Stream CreateDecryptingStream(Stream source, byte[] masterKey, byte[] iv)
    {
        return new PassthroughStream(source);
    }

    public string EncryptString(string plaintext, byte[] masterKey, byte[] iv)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
    }

    public string DecryptString(string ciphertext, byte[] masterKey, byte[] iv)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }

    private sealed class PassthroughStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);

        protected override void Dispose(bool disposing)
        {
            // Do NOT dispose inner — the caller (StreamingWriteHandle) disposes the backend stream separately
            if (disposing)
            {
                inner.Flush();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.FlushAsync();
            // Do NOT dispose inner
        }
    }
}
