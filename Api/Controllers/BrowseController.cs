using System.Net.Http.Headers;
using System.Text;
using System.Web;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared.Interfaces;
using Shared.Models;

namespace Api.Controllers;

[ApiController]
[Route("browse")]
public sealed class BrowseController(
    IEfRepository repository,
    IFileStorageService fileStorage,
    IEncryptionProviderFactory encryptionFactory,
    UserManager<User> users) : ControllerBase
{
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();

    [HttpGet]
    public async Task<IActionResult> ListUsers()
    {
        var userId = GetAuthenticatedUserId();
        if (userId is null)
            return Challenge();

        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.UserId == userId],
            project: d => d)).Where(d => d.HasFrontend(FrontendType.Http)).OrderBy(d => d.Name).ToList();

        return Content(RenderDirectoryPage("/ - File Server", "/browse",
            dataSources.Select(ds => (ds.Name, $"/browse/{ds.Id}/", (long?)null, (DateTimeOffset?)ds.CreatedAt))), "text/html");
    }

    [HttpGet("{dataSourceId:guid}/{**path}")]
    public async Task<IActionResult> ListOrDownload(Guid dataSourceId, string? path = null)
    {
        var (ds, masterKey, error) = await AuthorizeDataSource(dataSourceId);
        if (error is not null) return error;

        var encryption = encryptionFactory.GetProvider(ds!.Backend.EncryptionMethod);
        path = NormalizePath(path);

        // If path ends with a GUID, try to serve it as a file download
        if (TryParseFileId(path, out var fileId))
        {
            var matchFiles = (await FileDal.GetAll(
                filterExprs: [f => f.Id == fileId && f.DataSourceId == dataSourceId && f.UserId == ds!.UserId],
                project: f => f,
                maxResults: 1)).ToList();
            if (matchFiles.Count > 0)
            {
                var file = matchFiles.First();
                var iv = Convert.FromBase64String(file.IvBase64);
                var fullPath = encryption.DecryptString(file.OriginalFileName, masterKey!, iv);
                var fileName = System.IO.Path.GetFileName(fullPath);
                var contentType = file.ContentType is not null
                    ? encryption.DecryptString(file.ContentType, masterKey!, iv)
                    : "application/octet-stream";
                var stream = await fileStorage.OpenDecryptedStreamAsync(file, masterKey!);
                return File(stream, contentType, fileName);
            }
        }

        // Directory listing
        var allFiles = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == dataSourceId && f.UserId == ds!.UserId],
            project: f => f)).ToList();

        var entries = new List<(string Name, string Href, long? Size, DateTimeOffset? Modified)>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in allFiles)
        {
            var iv = Convert.FromBase64String(f.IvBase64);
            var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey!, iv);

            if (!fullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = fullPath[path.Length..];
            var slashIndex = relativePath.IndexOf('/');

            if (slashIndex < 0)
            {
                entries.Add((relativePath, $"/browse/{dataSourceId}/{path}{f.Id}", f.OriginalFileSize, f.CreatedAt));
            }
            else
            {
                var folderName = relativePath[..slashIndex];
                if (seenFolders.Add(folderName))
                    entries.Add((folderName, $"/browse/{dataSourceId}/{path}{folderName}/", null, null));
            }
        }

        entries = entries
            .OrderBy(e => e.Href.EndsWith('/') ? 0 : 1)
            .ThenBy(e => e.Name)
            .ToList();

        var displayPath = string.IsNullOrEmpty(path) ? $"/{ds!.Name}/" : $"/{ds!.Name}/{path}";
        var parentHref = string.IsNullOrEmpty(path)
            ? "/browse/"
            : $"/browse/{dataSourceId}/{GetParentPath(path)}";

        return Content(RenderDirectoryPage($"{displayPath} - File Server",
            $"/browse/{dataSourceId}/{path}", entries, parentHref), "text/html");
    }

    private Guid? GetAuthenticatedUserId()
    {
        if (User.Identity?.IsAuthenticated == true)
            return User.GetUserId();
        return null;
    }

    private async Task<(DataSource? ds, byte[]? masterKey, IActionResult? error)> AuthorizeDataSource(Guid dataSourceId)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId],
            project: d => d,
            maxResults: 1)).ToList();

        if (dataSources.Count == 0)
            return (null, null, NotFound());

        var ds = dataSources.First();
        var httpFrontend = ds.GetFrontend(FrontendType.Http);

        if (httpFrontend is null)
            return (null, null, NotFound());

        if (httpFrontend.AllowAnonymous)
        {
            var user = await users.FindByIdAsync(ds.UserId.ToString());
            if (user is null) return (null, null, NotFound());
            return (ds, Convert.FromBase64String(user.MasterKeyBase64), null);
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            var currentUserId = User.GetUserId();
            if (currentUserId != ds.UserId)
                return (null, null, Forbid());

            var user = await users.FindByIdAsync(currentUserId.ToString());
            return (ds, Convert.FromBase64String(user!.MasterKeyBase64), null);
        }

        var authHeader = Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authHeader["Basic ".Length..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var password = decoded.Contains(':') ? decoded[(decoded.IndexOf(':') + 1)..] : decoded;

            if (httpFrontend.Password is not null && password == httpFrontend.Password)
            {
                var user = await users.FindByIdAsync(ds.UserId.ToString());
                if (user is null) return (null, null, NotFound());
                return (ds, Convert.FromBase64String(user.MasterKeyBase64), null);
            }
        }

        Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{ds.Name}\"";
        return (null, null, Unauthorized());
    }

    private static string RenderDirectoryPage(string title, string currentPath,
        IEnumerable<(string Name, string Href, long? Size, DateTimeOffset? Modified)> entries,
        string? parentHref = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head>");
        sb.AppendLine($"<title>{HttpUtility.HtmlEncode(title)}</title>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: monospace; margin: 2em; background: #1a1a2e; color: #e0e0e0; }");
        sb.AppendLine("h1 { font-size: 1.4em; color: #00d4ff; border-bottom: 1px solid #333; padding-bottom: 0.5em; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; }");
        sb.AppendLine("th, td { text-align: left; padding: 4px 12px; }");
        sb.AppendLine("th { color: #888; font-weight: normal; border-bottom: 1px solid #333; }");
        sb.AppendLine("tr:hover { background: #16213e; }");
        sb.AppendLine("a { color: #00d4ff; text-decoration: none; }");
        sb.AppendLine("a:hover { text-decoration: underline; }");
        sb.AppendLine(".size { color: #888; text-align: right; }");
        sb.AppendLine(".date { color: #666; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>Index of {HttpUtility.HtmlEncode(currentPath)}</h1>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Name</th><th class=\"size\">Size</th><th class=\"date\">Modified</th></tr>");

        if (parentHref is not null)
        {
            sb.AppendLine($"<tr><td><a href=\"{parentHref}\">../</a></td><td class=\"size\">-</td><td class=\"date\">-</td></tr>");
        }

        foreach (var (name, href, size, modified) in entries)
        {
            var isDir = href.EndsWith('/');
            var displayName = isDir ? $"{HttpUtility.HtmlEncode(name)}/" : HttpUtility.HtmlEncode(name);
            var sizeStr = size.HasValue ? FormatSize(size.Value) : "-";
            var dateStr = modified.HasValue ? modified.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "-";
            sb.AppendLine($"<tr><td><a href=\"{href}\">{displayName}</a></td><td class=\"size\">{sizeStr}</td><td class=\"date\">{dateStr}</td></tr>");
        }

        sb.AppendLine("</table>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        path = path.Replace('\\', '/').Trim('/');
        return path.Length > 0 ? path + "/" : string.Empty;
    }

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? "" : trimmed[..(lastSlash + 1)];
    }

    private static bool TryParseFileId(string path, out Guid fileId)
    {
        fileId = Guid.Empty;
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var segment = lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
        return Guid.TryParse(segment, out fileId);
    }
}
