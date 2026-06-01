using System.Collections.Immutable;

namespace Api.Utilities;

public static class UrlUtility
{
    public static IReadOnlyDictionary<string, string> UrlToResource(string connectionStringUrl)
    {
        var isUrl = Uri.TryCreate(connectionStringUrl, UriKind.Absolute, out var url);
        if (!isUrl || url is null)
            return ImmutableDictionary.Create<string, string>();

        var connectionStringBuilder = new Dictionary<string, string>
        {
            ["Port"] = url.Port.ToString(),
            ["Host"] = url.Host,
            ["Username"] = url.UserInfo.Split(':').GetValue(0)?.ToString()!,
            ["Password"] = url.UserInfo.Split(':').GetValue(1)?.ToString()!,
            ["Database"] = url.LocalPath[1..],
            ["ApplicationName"] = "encrypted-file-server"
        };

        return connectionStringBuilder;
    }
}
