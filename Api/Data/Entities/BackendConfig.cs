using Shared.Contracts;
using Shared.Models;

namespace Api.Data.Entities;

public sealed class BackendConfig
{
    public BackendStorageType Protocol { get; set; } = BackendStorageType.FtpClient;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string BasePath { get; set; } = "/";
    public bool UseSsl { get; set; }
    public EncryptionMethod EncryptionMethod { get; set; } = EncryptionMethod.AesCtr256;

    /// <summary>
    /// User-provided master password for encrypting/decrypting files.
    /// The same password always derives the same AES-256 key (via PBKDF2),
    /// allowing re-attachment to an existing backend.
    /// </summary>
    public string MasterPassword { get; set; } = string.Empty;

    /// <summary>
    /// When enabled, files are compressed with Brotli before encryption.
    /// </summary>
    public bool UseCompression { get; set; }
}
