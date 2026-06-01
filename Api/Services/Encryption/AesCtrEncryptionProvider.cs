using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Shared.Interfaces;

namespace Api.Services.Encryption;

public sealed class AesCtrEncryptionProvider : IEncryptionProvider
{
    public string ProviderKey => "aes-ctr-256";

    public (Stream encryptingStream, byte[] iv) CreateEncryptingStream(Stream destination, byte[] masterKey)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        return (new AesCtrStream(destination, masterKey, iv, leaveOpen: true), iv);
    }

    public Stream CreateDecryptingStream(Stream source, byte[] masterKey, byte[] iv)
    {
        return new AesCtrStream(source, masterKey, iv, leaveOpen: false);
    }

    public string EncryptString(string plaintext, byte[] masterKey, byte[] iv)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        CtrXor(bytes, masterKey, iv);
        return Convert.ToBase64String(bytes);
    }

    public string DecryptString(string ciphertext, byte[] masterKey, byte[] iv)
    {
        var bytes = Convert.FromBase64String(ciphertext);
        CtrXor(bytes, masterKey, iv);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void CtrXor(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var ecb = aes.CreateEncryptor();

        var counter = (byte[])iv.Clone();
        var keystreamBlock = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            ecb.TransformBlock(counter, 0, 16, keystreamBlock, 0);
            IncrementCounter(counter);
            var blockLen = Math.Min(16, data.Length - offset);
            for (var j = 0; j < blockLen; j++)
                data[offset + j] ^= keystreamBlock[j];
        }
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
            if (++counter[i] != 0)
                break;
    }

    private sealed class AesCtrStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb;
        private readonly byte[] _counter;
        private readonly byte[] _keystreamBlock = new byte[16];
        private int _keystreamPos = 16;

        public AesCtrStream(Stream inner, byte[] key, byte[] iv, bool leaveOpen)
        {
            _inner = inner;
            _leaveOpen = leaveOpen;
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;
            _ecb = _aes.CreateEncryptor();
            _counter = (byte[])iv.Clone();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private void NextKeystreamBlock()
        {
            _ecb.TransformBlock(_counter, 0, 16, _keystreamBlock, 0);
            IncrementCounter(_counter);
            _keystreamPos = 0;
        }

        private void XorKeystream(Span<byte> data)
        {
            var pos = 0;
            while (pos < data.Length)
            {
                if (_keystreamPos >= 16)
                    NextKeystreamBlock();

                var available = 16 - _keystreamPos;
                var chunk = Math.Min(available, data.Length - pos);
                for (var i = 0; i < chunk; i++)
                    data[pos + i] ^= _keystreamBlock[_keystreamPos + i];

                _keystreamPos += chunk;
                pos += chunk;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _inner.Read(buffer, offset, count);
            XorKeystream(buffer.AsSpan(offset, bytesRead));
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var bytesRead = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
            XorKeystream(buffer.AsSpan(offset, bytesRead));
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var bytesRead = await _inner.ReadAsync(buffer, ct);
            XorKeystream(buffer.Span[..bytesRead]);
            return bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var rented = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                Buffer.BlockCopy(buffer, offset, rented, 0, count);
                XorKeystream(rented.AsSpan(0, count));
                _inner.Write(rented, 0, count);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var rented = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                Buffer.BlockCopy(buffer, offset, rented, 0, count);
                XorKeystream(rented.AsSpan(0, count));
                await _inner.WriteAsync(rented.AsMemory(0, count), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.Span.CopyTo(rented);
                XorKeystream(rented.AsSpan(0, buffer.Length));
                await _inner.WriteAsync(rented.AsMemory(0, buffer.Length), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _ecb.Dispose();
                _aes.Dispose();
                if (!_leaveOpen) _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _ecb.Dispose();
            _aes.Dispose();
            if (!_leaveOpen) await _inner.DisposeAsync();
        }
    }
}
