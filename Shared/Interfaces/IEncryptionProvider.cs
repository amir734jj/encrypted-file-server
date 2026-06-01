namespace Shared.Interfaces;

/// <summary>
/// Abstraction for the encryption algorithm used to encrypt/decrypt file streams.
/// Implementations: AES-256-CBC, AES-256-GCM, ChaCha20-Poly1305, etc.
/// </summary>
public interface IEncryptionProvider
{
    /// <summary>Unique key identifying this provider (e.g. "aes-cbc-256", "aes-gcm-256").</summary>
    string ProviderKey { get; }

    /// <summary>
    /// Wraps a destination stream with an encrypting layer.
    /// Returns the encrypting stream and the IV/nonce used (as raw bytes).
    /// The caller must dispose the returned stream to finalize encryption.
    /// </summary>
    (Stream encryptingStream, byte[] iv) CreateEncryptingStream(Stream destination, byte[] masterKey);

    /// <summary>
    /// Wraps a source stream with a decrypting layer.
    /// The returned stream reads plaintext; disposing it also disposes the source.
    /// </summary>
    Stream CreateDecryptingStream(Stream source, byte[] masterKey, byte[] iv);

    /// <summary>Encrypts a plaintext string. Returns base64-encoded ciphertext.</summary>
    string EncryptString(string plaintext, byte[] masterKey, byte[] iv);

    /// <summary>Decrypts a base64-encoded ciphertext string back to plaintext.</summary>
    string DecryptString(string ciphertext, byte[] masterKey, byte[] iv);
}
