namespace Api.Sftp;

/// <summary>
/// SFTP v3 protocol constants.
/// </summary>
internal static class SftpConstants
{
    // Packet types
    public const byte SSH_FXP_INIT = 1, SSH_FXP_VERSION = 2;
    public const byte SSH_FXP_OPEN = 3, SSH_FXP_CLOSE = 4, SSH_FXP_READ = 5, SSH_FXP_WRITE = 6;
    public const byte SSH_FXP_LSTAT = 7, SSH_FXP_FSTAT = 8, SSH_FXP_SETSTAT = 9, SSH_FXP_FSETSTAT = 10;
    public const byte SSH_FXP_OPENDIR = 11, SSH_FXP_READDIR = 12;
    public const byte SSH_FXP_REMOVE = 13, SSH_FXP_MKDIR = 14, SSH_FXP_RMDIR = 15;
    public const byte SSH_FXP_REALPATH = 16, SSH_FXP_STAT = 17;
    public const byte SSH_FXP_RENAME = 18;
    public const byte SSH_FXP_STATUS = 101, SSH_FXP_HANDLE = 102;
    public const byte SSH_FXP_DATA = 103, SSH_FXP_NAME = 104, SSH_FXP_ATTRS = 105;

    // Status codes
    public const uint SSH_FX_OK = 0, SSH_FX_EOF = 1, SSH_FX_NO_SUCH_FILE = 2;
    public const uint SSH_FX_PERMISSION_DENIED = 3, SSH_FX_FAILURE = 4, SSH_FX_OP_UNSUPPORTED = 8;

    // Open flags
    public const uint SSH_FXF_READ = 0x01, SSH_FXF_WRITE = 0x02, SSH_FXF_CREAT = 0x08, SSH_FXF_TRUNC = 0x10;

    // Attr flags
    public const uint ATTR_SIZE = 0x01, ATTR_UIDGID = 0x02, ATTR_PERMS = 0x04, ATTR_ACMODTIME = 0x08;

    // POSIX mode bits
    public const uint S_IFDIR = 0x4000, S_IFREG = 0x8000;
    public const uint DIR_MODE = S_IFDIR | 0x1ED;   // drwxr-xr-x
    public const uint FILE_MODE = S_IFREG | 0x1A4;  // -rw-r--r--
}
