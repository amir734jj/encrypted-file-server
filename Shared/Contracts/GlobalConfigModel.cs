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
}
