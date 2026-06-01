namespace Shared.Contracts;

public class GlobalConfigModel
{
    [GlobalConfigCol(Name = "FTP_PORT")]
    public int FtpPort { get; set; } = 2121;

    [GlobalConfigCol(Name = "FTP_PASSIVE_PORT_MIN")]
    public int FtpPassivePortMin { get; set; } = 50000;

    [GlobalConfigCol(Name = "FTP_PASSIVE_PORT_MAX")]
    public int FtpPassivePortMax { get; set; } = 50100;

    [GlobalConfigCol(Name = "MAX_UPLOAD_SIZE_MB")]
    public int MaxUploadSizeMb { get; set; } = 500;

    [GlobalConfigCol(Name = "STORAGE_BASE_PATH")]
    public string StorageBasePath { get; set; } = "storage";

    [GlobalConfigCol(Name = "ENCRYPTION_ALGORITHM")]
    public string EncryptionAlgorithm { get; set; } = "aes-cbc-256";

    [GlobalConfigCol(Name = "AUTO_ACTIVATE_USERS")]
    public bool AutoActivateUsers { get; set; }

    [GlobalConfigCol(Name = "ALLOW_REGISTRATION")]
    public bool AllowRegistration { get; set; } = true;
}
