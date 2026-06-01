using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Shared.Interfaces;

namespace Api.Services;

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

    private sealed class ChaChaEncryptStream(Stream inner, byte[] key, byte[] baseNonce) : Stream
    {
        private readonly byte[] _buffer = new byte[ChunkSize];
        private int _bufferPos;
        private long _chunkIndex;

        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private byte[] DeriveChunkNonce()
        {
            var nonce = new byte[NonceSize];
            Buffer.BlockCopy(baseNonce, 0, nonce, 0, NonceSize);
            var counter = BitConverter.GetBytes(_chunkIndex);
            for (var i = 0; i < Math.Min(counter.Length, NonceSize); i++)
                nonce[NonceSize - 1 - i] ^= counter[i];
            return nonce;
        }

        private void FlushChunk(bool isFinal)
        {
            if (_bufferPos == 0 && !isFinal) return;

            var plaintext = _buffer.AsSpan(0, _bufferPos);
            var ciphertext = new byte[_bufferPos];
            var tag = new byte[TagSize];
            var nonce = DeriveChunkNonce();

            using var chacha = new ChaCha20Poly1305(key);
            chacha.Encrypt(nonce, plaintext, ciphertext, tag);

            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, _bufferPos);
            inner.Write(header);
            inner.Write(tag);
            inner.Write(ciphertext);

            _bufferPos = 0;
            _chunkIndex++;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var pos = 0;
            while (pos < count)
            {
                var toCopy = Math.Min(count - pos, ChunkSize - _bufferPos);
                Buffer.BlockCopy(buffer, offset + pos, _buffer, _bufferPos, toCopy);
                _bufferPos += toCopy;
                pos += toCopy;
                if (_bufferPos >= ChunkSize) FlushChunk(false);
            }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushChunk(true);
                inner.Flush();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ChaChaDecryptStream(Stream inner, byte[] key, byte[] baseNonce) : Stream
    {
        private byte[]? _decryptedChunk;
        private int _chunkPos;
        private int _chunkLen;
        private long _chunkIndex;
        private bool _eof;

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private byte[] DeriveChunkNonce()
        {
            var nonce = new byte[NonceSize];
            Buffer.BlockCopy(baseNonce, 0, nonce, 0, NonceSize);
            var counter = BitConverter.GetBytes(_chunkIndex);
            for (var i = 0; i < Math.Min(counter.Length, NonceSize); i++)
                nonce[NonceSize - 1 - i] ^= counter[i];
            return nonce;
        }

        private bool ReadNextChunk()
        {
            var headerBuf = new byte[4];
            if (ReadExact(inner, headerBuf, 4) < 4) { _eof = true; return false; }
            var plainLen = BinaryPrimitives.ReadInt32LittleEndian(headerBuf);

            var tag = new byte[TagSize];
            if (ReadExact(inner, tag, TagSize) < TagSize) { _eof = true; return false; }

            var ciphertext = new byte[plainLen];
            if (ReadExact(inner, ciphertext, plainLen) < plainLen) { _eof = true; return false; }

            _decryptedChunk = new byte[plainLen];
            var nonce = DeriveChunkNonce();
            using var chacha = new ChaCha20Poly1305(key);
            chacha.Decrypt(nonce, ciphertext, tag, _decryptedChunk);
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                if (_decryptedChunk is null || _chunkPos >= _chunkLen)
                {
                    if (_eof || !ReadNextChunk()) break;
                }

                var available = _chunkLen - _chunkPos;
                var toCopy = Math.Min(available, count - totalRead);
                Buffer.BlockCopy(_decryptedChunk!, _chunkPos, buffer, offset + totalRead, toCopy);
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
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
