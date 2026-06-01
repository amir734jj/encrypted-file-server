namespace Shared.Contracts;

/// <summary>
/// Which encryption algorithm is used for file encryption.
/// </summary>
public enum EncryptionType
{
    AesCbc256 = 0,
    // AesGcm256 = 1,
    // ChaCha20Poly1305 = 2,
}
