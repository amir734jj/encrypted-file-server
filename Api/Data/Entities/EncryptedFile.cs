using Api.Interfaces;

namespace Api.Data.Entities;

public sealed class EncryptedFile : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid DataSourceId { get; set; }
    public DataSource? DataSource { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long OriginalFileSize { get; set; }

    /// <summary>
    /// Base64-encoded AES IV used to encrypt this file.
    /// </summary>
    public string IvBase64 { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
