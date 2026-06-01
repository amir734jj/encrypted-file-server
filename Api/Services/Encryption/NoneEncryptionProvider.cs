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
        return (destination, iv);
    }

    public Stream CreateDecryptingStream(Stream source, byte[] masterKey, byte[] iv)
    {
        return source;
    }

    public string EncryptString(string plaintext, byte[] masterKey, byte[] iv)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
    }

    public string DecryptString(string ciphertext, byte[] masterKey, byte[] iv)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
