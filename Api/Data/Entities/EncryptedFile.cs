using Api.Interfaces;
using Shared.Models;

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
    /// The encryption method used for this specific file.
    /// Falls back to the data source default for legacy files that don't have this set.
    /// </summary>
    public EncryptionMethod? EncryptionMethod { get; set; }

    /// <summary>
    /// Base64-encoded IV/nonce used to encrypt this file.
    /// </summary>
    public string IvBase64 { get; set; } = string.Empty;

    /// <summary>
    /// Whether the file content was compressed before encryption.
    /// </summary>
    public bool IsCompressed { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
