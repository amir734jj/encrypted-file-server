using System.Security.Cryptography;
using System.Text;
using Shared.Interfaces;

namespace Api.Services;

public sealed class AesCtrEncryptionProvider : IEncryptionProvider
{
    public string ProviderKey => "aes-ctr-256";

    public (Stream encryptingStream, byte[] iv) CreateEncryptingStream(Stream destination, byte[] masterKey)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        var transform = new AesCtrTransform(masterKey, iv);
        var cryptoStream = new CryptoStream(destination, transform, CryptoStreamMode.Write, leaveOpen: true);
        return (cryptoStream, iv);
    }

    public Stream CreateDecryptingStream(Stream source, byte[] masterKey, byte[] iv)
    {
        var transform = new AesCtrTransform(masterKey, iv);
        return new CryptoStream(source, transform, CryptoStreamMode.Read, leaveOpen: false);
    }

    public string EncryptString(string plaintext, byte[] masterKey, byte[] iv)
    {
        using var transform = new AesCtrTransform(masterKey, iv);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = transform.TransformFinalBlock(bytes, 0, bytes.Length);
        return Convert.ToBase64String(encrypted);
    }

    public string DecryptString(string ciphertext, byte[] masterKey, byte[] iv)
    {
        using var transform = new AesCtrTransform(masterKey, iv);
        var bytes = Convert.FromBase64String(ciphertext);
        var decrypted = transform.TransformFinalBlock(bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(decrypted);
    }

    private sealed class AesCtrTransform : ICryptoTransform
    {
        private readonly ICryptoTransform _ecbEncryptor;
        private readonly byte[] _counter;
        private readonly byte[] _keystreamBlock = new byte[16];

        public AesCtrTransform(byte[] key, byte[] iv)
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            _ecbEncryptor = aes.CreateEncryptor();
            _counter = (byte[])iv.Clone();
        }

        public int InputBlockSize => 16;
        public int OutputBlockSize => 16;
        public bool CanTransformMultipleBlocks => true;
        public bool CanReuseTransform => false;

        public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount,
            byte[] outputBuffer, int outputOffset)
        {
            for (var i = 0; i < inputCount; i += 16)
            {
                _ecbEncryptor.TransformBlock(_counter, 0, 16, _keystreamBlock, 0);
                IncrementCounter();

                var blockLen = Math.Min(16, inputCount - i);
                for (var j = 0; j < blockLen; j++)
                    outputBuffer[outputOffset + i + j] =
                        (byte)(inputBuffer[inputOffset + i + j] ^ _keystreamBlock[j]);
            }

            return inputCount;
        }

        public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
        {
            var output = new byte[inputCount];
            if (inputCount > 0)
            {
                _ecbEncryptor.TransformBlock(_counter, 0, 16, _keystreamBlock, 0);
                for (var i = 0; i < inputCount; i++)
                    output[i] = (byte)(inputBuffer[inputOffset + i] ^ _keystreamBlock[i]);
            }

            return output;
        }

        private void IncrementCounter()
        {
            for (var i = _counter.Length - 1; i >= 0; i--)
                if (++_counter[i] != 0)
                    break;
        }

        public void Dispose() => _ecbEncryptor.Dispose();
    }
}
