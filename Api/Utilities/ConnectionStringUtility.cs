using Api.Extensions;
using Npgsql;

namespace Api.Utilities;

public static class ConnectionStringUtility
{
    public static string ConnectionStringUrlToPgResource(string connectionStringUrl, Action<NpgsqlConnectionStringBuilder>? action = null)
    {
        var table = UrlUtility.UrlToResource(connectionStringUrl);
        if (!table.ContainKeys("Host", "Username", "Password", "Database", "ApplicationName"))
        {
            return connectionStringUrl; // Not a URL, return as-is (standard connection string)
        }

        if (!int.TryParse(table["Port"], out var port) || port <= 0)
        {
            port = 5432;
        }

        var connectionStringBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = table["Host"],
            Username = table["Username"],
            Password = table["Password"],
            Database = table["Database"],
            ApplicationName = table["ApplicationName"],
            SslMode = SslMode.Disable,
            Pooling = true,
            MaxPoolSize = 20,
            Port = port,
            CommandTimeout = 0,
            Timeout = (int)TimeSpan.FromMinutes(1).TotalSeconds
        };

        (action ?? (_ => { }))(connectionStringBuilder);
        return connectionStringBuilder.ToString();
    }
}
