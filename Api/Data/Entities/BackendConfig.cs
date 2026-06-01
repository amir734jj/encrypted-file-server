using Shared.Models;

namespace Api.Data.Entities;

public sealed class BackendConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string BasePath { get; set; } = "/";
    public bool UseSsl { get; set; }
    public EncryptionMethod EncryptionMethod { get; set; } = EncryptionMethod.AesCtr256;
}
