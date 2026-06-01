using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Shared.Interfaces;

namespace Api.Services.Encryption;

public sealed class ChaCha20EncryptionProvider : IEncryptionProvider
{
    public string ProviderKey => "chacha20-poly1305";

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ChunkSize = 64 * 1024;

    public (Stream encryptingStream, byte[] iv) CreateEncryptingStream(Stream destination, byte[] masterKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        return (new ChaChaEncryptStream(destination, masterKey, nonce), nonce);
    }

    public Stream CreateDecryptingStream(Stream source, byte[] masterKey, byte[] iv)
    {
        return new ChaChaDecryptStream(source, masterKey, iv);
    }

    public string EncryptString(string plaintext, byte[] masterKey, byte[] iv)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[data.Length];
        var tag = new byte[TagSize];
        using var chacha = new ChaCha20Poly1305(masterKey);
        chacha.Encrypt(iv, data, ciphertext, tag);
        var result = new byte[tag.Length + ciphertext.Length];
        Buffer.BlockCopy(tag, 0, result, 0, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, tag.Length, ciphertext.Length);
        return Convert.ToBase64String(result);
    }

    public string DecryptString(string ciphertext, byte[] masterKey, byte[] iv)
    {
        var combined = Convert.FromBase64String(ciphertext);
        var tag = combined.AsSpan(0, TagSize);
        var encrypted = combined.AsSpan(TagSize);
        var plaintext = new byte[encrypted.Length];
        using var chacha = new ChaCha20Poly1305(masterKey);
        chacha.Decrypt(iv, encrypted, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Encrypts data in 64 KB AEAD chunks: [4-byte len][16-byte tag][ciphertext].
    /// All buffers are pre-allocated; zero per-chunk heap allocations.
    /// Crypto instance is reused across chunks.
    /// </summary>
    private sealed class ChaChaEncryptStream : Stream
    {
        private readonly Stream _inner;
        private readonly ChaCha20Poly1305 _chacha;
        private readonly byte[] _baseNonce;
        private readonly byte[] _plaintextBuf = new byte[ChunkSize];
        private readonly byte[] _ciphertextBuf = new byte[ChunkSize];
        private readonly byte[] _tagBuf = new byte[TagSize];
        private readonly byte[] _nonceBuf = new byte[NonceSize];
        private readonly byte[] _headerBuf = new byte[4];
        private int _bufferPos;
        private long _chunkIndex;
        private bool _disposed;

        public ChaChaEncryptStream(Stream inner, byte[] key, byte[] baseNonce)
        {
            _inner = inner;
            _chacha = new ChaCha20Poly1305(key);
            _baseNonce = baseNonce;
        }

        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private void DeriveChunkNonce()
        {
            Buffer.BlockCopy(_baseNonce, 0, _nonceBuf, 0, NonceSize);
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(counter, _chunkIndex);
            for (var i = 0; i < Math.Min(counter.Length, NonceSize); i++)
                _nonceBuf[NonceSize - 1 - i] ^= counter[i];
        }

        private void EncryptAndWriteChunk()
        {
            if (_bufferPos == 0) return;

            DeriveChunkNonce();
            _chacha.Encrypt(
                _nonceBuf,
                _plaintextBuf.AsSpan(0, _bufferPos),
                _ciphertextBuf.AsSpan(0, _bufferPos),
                _tagBuf);

            BinaryPrimitives.WriteInt32LittleEndian(_headerBuf, _bufferPos);
            _inner.Write(_headerBuf, 0, 4);
            _inner.Write(_tagBuf, 0, TagSize);
            _inner.Write(_ciphertextBuf, 0, _bufferPos);

            _bufferPos = 0;
            _chunkIndex++;
        }

        private async ValueTask EncryptAndWriteChunkAsync(CancellationToken ct)
        {
            if (_bufferPos == 0) return;

            DeriveChunkNonce();
            _chacha.Encrypt(
                _nonceBuf,
                _plaintextBuf.AsSpan(0, _bufferPos),
                _ciphertextBuf.AsSpan(0, _bufferPos),
                _tagBuf);

            BinaryPrimitives.WriteInt32LittleEndian(_headerBuf, _bufferPos);
            await _inner.WriteAsync(_headerBuf.AsMemory(0, 4), ct);
            await _inner.WriteAsync(_tagBuf.AsMemory(0, TagSize), ct);
            await _inner.WriteAsync(_ciphertextBuf.AsMemory(0, _bufferPos), ct);

            _bufferPos = 0;
            _chunkIndex++;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var pos = 0;
            while (pos < count)
            {
                var toCopy = Math.Min(count - pos, ChunkSize - _bufferPos);
                Buffer.BlockCopy(buffer, offset + pos, _plaintextBuf, _bufferPos, toCopy);
                _bufferPos += toCopy;
                pos += toCopy;
                if (_bufferPos >= ChunkSize) EncryptAndWriteChunk();
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var pos = 0;
            while (pos < count)
            {
                var toCopy = Math.Min(count - pos, ChunkSize - _bufferPos);
                Buffer.BlockCopy(buffer, offset + pos, _plaintextBuf, _bufferPos, toCopy);
                _bufferPos += toCopy;
                pos += toCopy;
                if (_bufferPos >= ChunkSize) await EncryptAndWriteChunkAsync(ct);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var pos = 0;
            while (pos < buffer.Length)
            {
                var toCopy = Math.Min(buffer.Length - pos, ChunkSize - _bufferPos);
                buffer.Span.Slice(pos, toCopy).CopyTo(_plaintextBuf.AsSpan(_bufferPos));
                _bufferPos += toCopy;
                pos += toCopy;
                if (_bufferPos >= ChunkSize) await EncryptAndWriteChunkAsync(ct);
            }
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                EncryptAndWriteChunk();
                _inner.Flush();
                _chacha.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await EncryptAndWriteChunkAsync(CancellationToken.None);
                await _inner.FlushAsync();
                _chacha.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Reads and decrypts chunked AEAD data. All buffers are pre-allocated (2 × 64 KB).
    /// Validates chunk length headers to prevent memory bombs.
    /// Supports both sync and async I/O on the underlying stream.
    /// </summary>
    private sealed class ChaChaDecryptStream : Stream
    {
        private readonly Stream _inner;
        private readonly ChaCha20Poly1305 _chacha;
        private readonly byte[] _baseNonce;
        private readonly byte[] _headerBuf = new byte[4];
        private readonly byte[] _tagBuf = new byte[TagSize];
        private readonly byte[] _ciphertextBuf = new byte[ChunkSize];
        private readonly byte[] _decryptedBuf = new byte[ChunkSize];
        private readonly byte[] _nonceBuf = new byte[NonceSize];
        private int _chunkPos;
        private int _chunkLen;
        private long _chunkIndex;
        private bool _eof;
        private bool _disposed;

        public ChaChaDecryptStream(Stream inner, byte[] key, byte[] baseNonce)
        {
            _inner = inner;
            _chacha = new ChaCha20Poly1305(key);
            _baseNonce = baseNonce;
        }

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private void DeriveChunkNonce()
        {
            Buffer.BlockCopy(_baseNonce, 0, _nonceBuf, 0, NonceSize);
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(counter, _chunkIndex);
            for (var i = 0; i < Math.Min(counter.Length, NonceSize); i++)
                _nonceBuf[NonceSize - 1 - i] ^= counter[i];
        }

        private bool ReadNextChunk()
        {
            if (ReadExact(_inner, _headerBuf, 4) < 4) { _eof = true; return false; }
            var plainLen = BinaryPrimitives.ReadInt32LittleEndian(_headerBuf);

            if (plainLen <= 0 || plainLen > ChunkSize)
                throw new InvalidDataException($"Invalid chunk length {plainLen}; max allowed is {ChunkSize}.");

            if (ReadExact(_inner, _tagBuf, TagSize) < TagSize) { _eof = true; return false; }
            if (ReadExact(_inner, _ciphertextBuf, plainLen) < plainLen) { _eof = true; return false; }

            DeriveChunkNonce();
            _chacha.Decrypt(
                _nonceBuf,
                _ciphertextBuf.AsSpan(0, plainLen),
                _tagBuf,
                _decryptedBuf.AsSpan(0, plainLen));
            _chunkPos = 0;
            _chunkLen = plainLen;
            _chunkIndex++;
            return true;
        }

        private async Task<bool> ReadNextChunkAsync(CancellationToken ct)
        {
            if (await ReadExactAsync(_inner, _headerBuf, 4, ct) < 4) { _eof = true; return false; }
            var plainLen = BinaryPrimitives.ReadInt32LittleEndian(_headerBuf);

            if (plainLen <= 0 || plainLen > ChunkSize)
                throw new InvalidDataException($"Invalid chunk length {plainLen}; max allowed is {ChunkSize}.");

            if (await ReadExactAsync(_inner, _tagBuf, TagSize, ct) < TagSize) { _eof = true; return false; }
            if (await ReadExactAsync(_inner, _ciphertextBuf, plainLen, ct) < plainLen) { _eof = true; return false; }

            DeriveChunkNonce();
            _chacha.Decrypt(
                _nonceBuf,
                _ciphertextBuf.AsSpan(0, plainLen),
                _tagBuf,
                _decryptedBuf.AsSpan(0, plainLen));
            _chunkPos = 0;
            _chunkLen = plainLen;
            _chunkIndex++;
            return true;
        }

        private static int ReadExact(Stream s, byte[] buffer, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = s.Read(buffer, totalRead, count - totalRead);
                if (read == 0) return totalRead;
                totalRead += read;
            }
            return totalRead;
        }

        private static async Task<int> ReadExactAsync(Stream s, byte[] buffer, int count, CancellationToken ct)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = await s.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
                if (read == 0) return totalRead;
                totalRead += read;
            }
            return totalRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                if (_chunkPos >= _chunkLen)
                {
                    if (_eof || !ReadNextChunk()) break;
                }

                var available = _chunkLen - _chunkPos;
                var toCopy = Math.Min(available, count - totalRead);
                Buffer.BlockCopy(_decryptedBuf, _chunkPos, buffer, offset + totalRead, toCopy);
                _chunkPos += toCopy;
                totalRead += toCopy;
            }
            return totalRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                if (_chunkPos >= _chunkLen)
                {
                    if (_eof || !await ReadNextChunkAsync(ct)) break;
                }

                var available = _chunkLen - _chunkPos;
                var toCopy = Math.Min(available, count - totalRead);
                Buffer.BlockCopy(_decryptedBuf, _chunkPos, buffer, offset + totalRead, toCopy);
                _chunkPos += toCopy;
                totalRead += toCopy;
            }
            return totalRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                if (_chunkPos >= _chunkLen)
                {
                    if (_eof || !await ReadNextChunkAsync(ct)) break;
                }

                var available = _chunkLen - _chunkPos;
                var toCopy = Math.Min(available, buffer.Length - totalRead);
                _decryptedBuf.AsSpan(_chunkPos, toCopy).CopyTo(buffer.Span[totalRead..]);
                _chunkPos += toCopy;
                totalRead += toCopy;
            }
            return totalRead;
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _chacha.Dispose();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _chacha.Dispose();
                await _inner.DisposeAsync();
            }
            GC.SuppressFinalize(this);
        }
    }
}
