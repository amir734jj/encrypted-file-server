namespace Shared.Models;

public enum EncryptionMethod
{
    None,
    AesCtr256,
    AesGcm256,
    ChaCha20Poly1305
}
