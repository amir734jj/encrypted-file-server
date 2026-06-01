namespace Api.Utilities;

public static class ConnectionStringUtility
{
    /// <summary>
    /// Converts a DATABASE_URL (e.g. postgres://user:pass@host:port/db) to a Npgsql connection string.
    /// If already a standard connection string, returns as-is.
    /// </summary>
    public static string ConnectionStringUrlToPgResource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(connectionString));

        if (!connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        var uri = new Uri(connectionString);
        var userInfo = uri.UserInfo.Split(':');
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');

        return $"Host={host};Port={port};Database={database};Username={user};Password={password};SSL Mode=Prefer;Trust Server Certificate=true;";
    }
}
