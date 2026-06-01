using System.Security.Cryptography;
using Shared.Interfaces;

namespace Api.Services;

public sealed class AesCbcEncryptionProvider : IEncryptionProvider
{
    public string ProviderKey => "aes-cbc-256";

    public (Stream encryptingStream, byte[] iv) CreateEncryptingStream(Stream destination, byte[] masterKey)
    {
        var aes = Aes.Create();
        aes.Key = masterKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        var iv = aes.IV;
        var encryptor = aes.CreateEncryptor();
        var cryptoStream = new CryptoStream(destination, encryptor, CryptoStreamMode.Write, leaveOpen: true);

        return (cryptoStream, iv);
    }

    public Stream CreateDecryptingStream(Stream source, byte[] masterKey, byte[] iv)
    {
        var aes = Aes.Create();
        aes.Key = masterKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var decryptor = aes.CreateDecryptor();
        return new CryptoStream(source, decryptor, CryptoStreamMode.Read, leaveOpen: false);
    }
}
