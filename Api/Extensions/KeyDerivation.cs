using System.Security.Cryptography;
using System.Text;

namespace Api.Extensions;

public static class KeyDerivation
{
    private static readonly byte[] AppSalt = "encrypted-file-server"u8.ToArray();

    /// <summary>
    /// Derives a deterministic 256-bit AES key from a user-provided master password.
    /// The same password always produces the same key, enabling re-attachment
    /// to an existing backend's encrypted files.
    /// </summary>
    public static byte[] DeriveKey(string masterPassword)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(masterPassword),
            AppSalt,
            100_000,
            HashAlgorithmName.SHA256,
            32); // 256 bits
    }
}
